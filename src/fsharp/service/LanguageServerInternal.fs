namespace Microsoft.FSharp.Compiler.LSP

open System
open System.IO
open System.Collections.Generic
open System.Diagnostics

open Microsoft.FSharp.Compiler
open Microsoft.FSharp.Compiler.CompileOptions
open Microsoft.FSharp.Compiler.Range
open Microsoft.FSharp.Compiler.CompileOps
open Microsoft.FSharp.Compiler.AbstractIL.Internal.Library 
open Microsoft.FSharp.Compiler.SourceCodeServices

open Internal.Utilities
open Internal.Utilities.Collections

// Most of the this implementation is copied from service.fs

/// An open source file that can be typechecked incrementally
[<Sealed>]
type LSFile(filename: string, dispose: unit -> unit) = 
    member f.FileName = filename
    interface IDisposable with 
        member f.Dispose() = dispose()
        
type FileName = string
type Source = string        
type FilePath = string
type ProjectPath = string
type FileVersion = int

type ParseCacheLockToken() = interface LockToken
type ScriptClosureCacheToken() = interface LockToken

[<AutoOpen>]
module Helpers = 
    // Look for DLLs in the location of the service DLL first.
    let defaultFSharpBinariesDir = FSharpEnvironment.BinFolderOfDefaultFSharpCompiler(Some(typeof<FSharpCheckFileAnswer>.Assembly.Location)).Value
    // We're just going to run single-threaded
    let ctok = CompilationThreadToken()
    // Defaults from FSharpChecker.Create
    let projectCacheSize = 3
    let frameworkTcImportsCacheStrongSize = 8
    let keepAssemblyContents = false
    let keepAllBackgroundResolutions = true
    let maxTimeShareMilliseconds = 100L
    let tryGetMetadataSnapshot = (fun _ -> None)
    let maxTypeCheckErrorsOutOfProjectContext = 3
    let legacyReferenceResolver = SimulatedMSBuildReferenceResolver.GetBestAvailableResolver()

/// A single project
[<Sealed>]
type LSDelegate() as self = 
    let openFiles = Dictionary<string, LSFile>()

    // STATIC ROOT: FSharpLanguageServiceTestable.FSharpChecker.backgroundCompiler.reactor: The one and only Reactor
    let reactor = Reactor.Singleton
    let reactorOps = 
        { new IReactorOperations with 
                member __.EnqueueAndAwaitOpAsync (userOpName, opName, opArg, op) = reactor.EnqueueAndAwaitOpAsync (userOpName, opName, opArg, op)
                member __.EnqueueOp (userOpName, opName, opArg, op) = reactor.EnqueueOp (userOpName, opName, opArg, op) }
    // Events
    let beforeFileChecked = Event<string * obj option>()
    let fileParsed = Event<string * obj option>()
    let fileChecked = Event<string * obj option>()
    let projectChecked = Event<string * obj option>()

    // STATIC ROOT: FSharpLanguageServiceTestable.FSharpChecker.backgroundCompiler.scriptClosureCache 
    /// Information about the derived script closure.
    let scriptClosureCache = 
        MruCache<ScriptClosureCacheToken, FSharpProjectOptions, LoadClosure>(projectCacheSize, 
            areSame=FSharpProjectOptions.AreSameForChecking, 
            areSimilar=FSharpProjectOptions.UseSameProject)

    let scriptClosureCacheLock = Lock<ScriptClosureCacheToken>()
    let frameworkTcImportsCache = FrameworkImportsCache(frameworkTcImportsCacheStrongSize)

    /// CreateOneIncrementalBuilder (for background type checking). Note that fsc.fs also
    /// creates an incremental builder used by the command line compiler.
    let CreateOneIncrementalBuilder (options:FSharpProjectOptions) = 
        cancellable {
            let projectReferences =  
                [ for (nm,opts) in options.ReferencedProjects do
                    
                    // Don't use cross-project references for FSharp.Core, since various bits of code require a concrete FSharp.Core to exist on-disk.
                    // The only solutions that have these cross-project references to FSharp.Core are VisualFSharp.sln and FSharp.sln. The only ramification
                    // of this is that you need to build FSharp.Core to get intellisense in those projects.
                    if (try Path.GetFileNameWithoutExtension(nm) with _ -> "") <> "FSharp.Core" then
                        yield
                            { new IProjectReference with 
                                member x.EvaluateRawContents(ctok) = 
                                    cancellable {
                                        let! r = self.ParseAndCheckProjectImpl(opts, ctok)
                                        return r.RawFSharpAssemblyData 
                                    }
                                member x.TryGetLogicalTimeStamp(cache, ctok) = 
                                    self.TryGetLogicalTimeStampForProject(cache, ctok, opts)
                                member x.FileName = nm } ]

            let loadClosure = scriptClosureCacheLock.AcquireLock (fun ltok -> scriptClosureCache.TryGet (ltok, options))
            let! builderOpt, diagnostics = 
                IncrementalBuilder.TryCreateBackgroundBuilderForProjectOptions
                        (ctok, legacyReferenceResolver, defaultFSharpBinariesDir, frameworkTcImportsCache, loadClosure, Array.toList options.SourceFiles, 
                        Array.toList options.OtherOptions, projectReferences, options.ProjectDirectory, 
                        options.UseScriptResolutionRules, keepAssemblyContents, keepAllBackgroundResolutions, maxTimeShareMilliseconds,
                        tryGetMetadataSnapshot)

            // We're putting the builder in the cache, so increment its count.
            let decrement = IncrementalBuilder.KeepBuilderAlive builderOpt

            match builderOpt with 
            | None -> ()
            | Some builder -> 

                // Register the behaviour that responds to CCUs being invalidated because of type
                // provider Invalidate events. This invalidates the configuration in the build.
                builder.ImportedCcusInvalidated.Add (fun _ -> 
                    self.InvalidateConfiguration(options))

                // Register the callback called just before a file is typechecked by the background builder (without recording
                // errors or intellisense information).
                //
                // This indicates to the UI that the file type check state is dirty. If the file is open and visible then 
                // the UI will sooner or later request a typecheck of the file, recording errors and intellisense information.
                builder.BeforeFileChecked.Add (fun file -> beforeFileChecked.Trigger(file, options.ExtraProjectInfo))
                builder.FileParsed.Add (fun file -> fileParsed.Trigger(file, options.ExtraProjectInfo))
                builder.FileChecked.Add (fun file -> fileChecked.Trigger(file, options.ExtraProjectInfo))
                builder.ProjectChecked.Add (fun () -> projectChecked.Trigger (options.ProjectFileName, options.ExtraProjectInfo))

            return (builderOpt, diagnostics, decrement)
        }

    // STATIC ROOT: FSharpLanguageServiceTestable.FSharpChecker.backgroundCompiler.incrementalBuildersCache. This root typically holds more 
    // live information than anything else in the F# Language Service, since it holds up to 3 (projectCacheStrongSize) background project builds
    // strongly.
    // 
    /// Cache of builds keyed by options.        
    let incrementalBuildersCache = 
        MruCache<CompilationThreadToken, FSharpProjectOptions, (IncrementalBuilder option * FSharpErrorInfo[] * IDisposable)>
                (keepStrongly=projectCacheSize, keepMax=projectCacheSize, 
                 areSame =  FSharpProjectOptions.AreSameForChecking, 
                 areSimilar =  FSharpProjectOptions.UseSameProject,
                 requiredToKeep=(fun (builderOpt,_,_) -> match builderOpt with None -> false | Some (b:IncrementalBuilder) -> b.IsBeingKeptAliveApartFromCacheEntry),
                 onDiscard = (fun (_, _, decrement:IDisposable) -> decrement.Dispose()))

    let getOrCreateBuilderAndKeepAlive (options) =
      cancellable {
          RequireCompilationThread ctok
          match incrementalBuildersCache.TryGet (ctok, options) with
          | Some (builderOpt,creationErrors,_) -> 
              Logger.Log LogCompilerFunctionId.Service_IncrementalBuildersCache_BuildingNewCache
              let decrement = IncrementalBuilder.KeepBuilderAlive builderOpt
              return builderOpt,creationErrors, decrement
          | None -> 
              Logger.Log LogCompilerFunctionId.Service_IncrementalBuildersCache_GettingCache
              let! (builderOpt,creationErrors,_) as info = CreateOneIncrementalBuilder (options)
              incrementalBuildersCache.Set (ctok, options, info)
              let decrement = IncrementalBuilder.KeepBuilderAlive builderOpt
              return builderOpt, creationErrors, decrement
      }

    let toAsync e =    
        async { 
          let! ct = Async.CancellationToken
          return! 
             Async.FromContinuations(fun (cont, econt, ccont) -> 
               // Run the computation synchronously using the given cancellation token
               let res = try Choice1Of2 (Cancellable.run ct e) with err -> Choice2Of2 err
               match res with 
               | Choice1Of2 (ValueOrCancelled.Value v) -> cont v
               | Choice1Of2 (ValueOrCancelled.Cancelled err) -> ccont err
               | Choice2Of2 err -> econt err) 
        }

    let MakeCheckFileResultsEmpty(filename, creationErrors) = 
        FSharpCheckFileResults (filename, creationErrors, None, [| |], None, reactorOps, keepAssemblyContents)

    let MakeCheckFileResults(filename, options:FSharpProjectOptions, builder, scope, dependencyFiles, creationErrors, parseErrors, tcErrors) = 
        let errors = 
            [| yield! creationErrors 
               yield! parseErrors
               if options.IsIncompleteTypeCheckEnvironment then 
                    yield! Seq.truncate maxTypeCheckErrorsOutOfProjectContext tcErrors
               else 
                    yield! tcErrors |]
                
        FSharpCheckFileResults (filename, errors, Some scope, dependencyFiles, Some builder, reactorOps, keepAssemblyContents)

    let MakeCheckFileAnswer(filename, tcFileResult, options:FSharpProjectOptions, builder, dependencyFiles, creationErrors, parseErrors, tcErrors) = 
        match tcFileResult with 
        | Parser.TypeCheckAborted.Yes  ->  FSharpCheckFileAnswer.Aborted                
        | Parser.TypeCheckAborted.No scope -> FSharpCheckFileAnswer.Succeeded(MakeCheckFileResults(filename, options, builder, scope, dependencyFiles, creationErrors, parseErrors, tcErrors))
    
    member d.GetParsingOptionsFromCommandLineArgs(initialSourceFiles, argv, ?isInteractive) =
        let isInteractive = defaultArg isInteractive false
        use errorScope = new ErrorScope()
        let tcConfigBuilder = TcConfigBuilder.Initial

        // Apply command-line arguments and collect more source files if they are in the arguments
        let sourceFilesNew = ApplyCommandLineArgs(tcConfigBuilder, initialSourceFiles, argv)
        FSharpParsingOptions.FromTcConfigBuidler(tcConfigBuilder, Array.ofList sourceFilesNew, isInteractive), errorScope.Diagnostics

    member d.GetParsingOptionsFromProjectOptions(options): FSharpParsingOptions * _ =
        let sourceFiles = List.ofArray options.SourceFiles
        let argv = List.ofArray options.OtherOptions
        d.GetParsingOptionsFromCommandLineArgs(sourceFiles, argv, options.UseScriptResolutionRules)

    member d.GetProjectOptionsFromCommandLineArgs(projectFileName, argv, ?loadedTimeStamp, ?extraProjectInfo: obj) = 
        let loadedTimeStamp = defaultArg loadedTimeStamp DateTime.MaxValue // Not 'now', we don't want to force reloading
        { ProjectFileName = projectFileName
          ProjectId = None
          SourceFiles = [| |] // the project file names will be inferred from the ProjectOptions
          OtherOptions = argv 
          ReferencedProjects= [| |]  
          IsIncompleteTypeCheckEnvironment = false
          UseScriptResolutionRules = false
          LoadTime = loadedTimeStamp
          UnresolvedReferences = None
          OriginalLoadReferences=[]
          ExtraProjectInfo=extraProjectInfo
          Stamp = None }

    /// Parse and typecheck the whole project (the implementation, called recursively as project graph is evaluated)
    member d.ParseAndCheckProjectImpl(options, ctok) : Cancellable<FSharpCheckProjectResults> =
      cancellable {
        let! builderOpt,creationErrors,decrement = getOrCreateBuilderAndKeepAlive (options)
        use _unwind = decrement
        match builderOpt with 
        | None -> 
            return FSharpCheckProjectResults (options.ProjectFileName, None, keepAssemblyContents, creationErrors, None)
        | Some builder -> 
            let! (tcProj, ilAssemRef, tcAssemblyDataOpt, tcAssemblyExprOpt)  = builder.GetCheckResultsAndImplementationsForProject(ctok)
            let errorOptions = tcProj.TcConfig.errorSeverityOptions
            let fileName = TcGlobals.DummyFileNameForRangesWithoutASpecificLocation
            let errors = [| yield! creationErrors; yield! ErrorHelpers.CreateErrorInfos (errorOptions, true, fileName, tcProj.TcErrors) |]
            return FSharpCheckProjectResults (options.ProjectFileName, Some tcProj.TcConfig, keepAssemblyContents, errors, 
                                              Some(tcProj.TcGlobals, tcProj.TcImports, tcProj.TcState.Ccu, tcProj.TcState.CcuSig, 
                                                   tcProj.TcSymbolUses, tcProj.TopAttribs, tcAssemblyDataOpt, ilAssemRef, 
                                                   tcProj.TcEnvAtEnd.AccessRights, tcAssemblyExprOpt, Array.ofList tcProj.TcDependencyFiles))
      }

    member bc.CheckOneFileImpl
        (parseResults: FSharpParseFileResults,
         source: string,
         fileName: string,
         options: FSharpProjectOptions,
         textSnapshotInfo: obj option,
         builder: IncrementalBuilder,
         tcPrior: PartialCheckResults,
         creationErrors: FSharpErrorInfo[],
         userOpName: string) = 
    
        async {
            // Get additional script #load closure information if applicable.
            // For scripts, this will have been recorded by GetProjectOptionsFromScript.
            let loadClosure = scriptClosureCacheLock.AcquireLock (fun ltok -> scriptClosureCache.TryGet (ltok, options))
            let! tcErrors, tcFileResult = 
                Parser.CheckOneFile(parseResults, source, fileName, options.ProjectFileName, tcPrior.TcConfig, tcPrior.TcGlobals, tcPrior.TcImports, 
                                    tcPrior.TcState, loadClosure, tcPrior.TcErrors, reactorOps, (fun () -> builder.IsAlive), textSnapshotInfo, userOpName)
            return MakeCheckFileAnswer(fileName, tcFileResult, options, builder, Array.ofList tcPrior.TcDependencyFiles, creationErrors, parseResults.Errors, tcErrors)
        }

    /// Type-check the result obtained by parsing. Force the evaluation of the antecedent type checking context if needed.
    member bc.CheckFileInProject(parseResults: FSharpParseFileResults, filename, source, options, textSnapshotInfo, userOpName) =
        async {
            let! builderOpt,creationErrors, decrement = toAsync(getOrCreateBuilderAndKeepAlive(options))
            use _unwind = decrement
            match builderOpt with
            | None -> return FSharpCheckFileAnswer.Succeeded(MakeCheckFileResultsEmpty(filename, creationErrors))
            | Some builder -> 
                let! tcPrior = toAsync(builder.GetCheckResultsBeforeFileInProject (ctok, filename))
                let parseTreeOpt = parseResults.ParseTree |> Option.map builder.DeduplicateParsedInputModuleNameInProject
                let parseResultsAterDeDuplication = FSharpParseFileResults(parseResults.Errors, parseTreeOpt, parseResults.ParseHadErrors, parseResults.DependencyFiles)
                let! checkAnswer = bc.CheckOneFileImpl(parseResultsAterDeDuplication, source, filename, options, textSnapshotInfo, builder, tcPrior, creationErrors, userOpName)
                return checkAnswer
        }

    /// Get the timestamp that would be on the output if fully built immediately
    member d.TryGetLogicalTimeStampForProject(cache, ctok, options: FSharpProjectOptions) =

        // NOTE: This creation of the background builder is currently run as uncancellable.  Creating background builders is generally
        // cheap though the timestamp computations look suspicious for transitive project references.
        let builderOpt,_creationErrors,decrement = getOrCreateBuilderAndKeepAlive (options) |> Cancellable.runWithoutCancellation
        use _unwind = decrement
        match builderOpt with 
        | None -> None
        | Some builder -> Some (builder.GetLogicalTimeStampForProject(cache, ctok))
            
    member d.InvalidateConfiguration(options : FSharpProjectOptions) =
        // If there was a similar entry then re-establish an empty builder .  This is a somewhat arbitrary choice - it
        // will have the effect of releasing memory associated with the previous builder, but costs some time.
        if incrementalBuildersCache.ContainsSimilarKey (ctok, options) then

            // We do not need to decrement here - the onDiscard function is called each time an entry is pushed out of the build cache,
            // including by incrementalBuildersCache.Set.
            let newBuilderInfo = CreateOneIncrementalBuilder (options) |> Cancellable.runWithoutCancellation
            incrementalBuildersCache.Set(ctok, options, newBuilderInfo)

    /// Parse a file
    /// This is useful for operations like navigation that don't require a full typecheck
    /// TODO: implement resolve operation that resolves exported symbols without doing a full typecheck
    member d.Parse(options, filename, source) = 
        let parseErrors, parseTreeOpt, anyErrors = Parser.parseFile(source, filename, options, "")
        FSharpParseFileResults(parseErrors, parseTreeOpt, anyErrors, options.SourceFiles)

    /// Open a file for checking
    /// An open file can be checked incrementally by specifying a "focus"
    member d.Open(_options: FSharpProjectOptions, filename) = 
        let dispose() = openFiles.Remove(filename) |> ignore
        let result = new LSFile(filename, dispose)
        openFiles.Add(filename, result)
        result

    /// Check the entire file, regardless of what has been checked previously
    member d.CheckFully(options: FSharpProjectOptions, file: LSFile, source) =
        async {
            let parseOptions, _ = d.GetParsingOptionsFromProjectOptions(options)
            let parsed = d.Parse(parseOptions, file.FileName, source)
            let! result = d.CheckFileInProject(parsed, file.FileName, source, options, None, "")
            return result
        }

    /// Check the expression around `focus`, and anything else that has been edited
    /// If the file has been extensively edited, this may fall back on `CheckFully`
    member d.CheckIncrementally(options: FSharpProjectOptions, file: LSFile, source, _focus: pos) =
        d.CheckFully(options, file, source)