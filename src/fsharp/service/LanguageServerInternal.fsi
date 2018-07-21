namespace Microsoft.FSharp.Compiler.LSP

open System
open Microsoft.FSharp.Compiler.Range
open Microsoft.FSharp.Compiler.SourceCodeServices

/// An open source file that can be typechecked incrementally
/// You should `Dispose()` this file when the user closes it
[<Sealed>]
type public LSFile = 
    member FileName: string
    interface IDisposable

/// LSDelegate is a simplified version of FSharpChecker focused on the language server protocol
[<Sealed>]
type public LSDelegate = 
    new: unit -> LSDelegate

    /// <summary>
    /// <para>Get the FSharpProjectOptions implied by a set of command line arguments.</para>
    /// </summary>
    ///
    /// <param name="projectFileName">Used to differentiate between projects and for the base directory of the project.</param>
    /// <param name="argv">The command line arguments for the project build.</param>
    /// <param name="loadedTimeStamp">Indicates when the script was loaded into the editing environment,
    /// so that an 'unload' and 'reload' action will cause the script to be considered as a new project,
    /// so that references are re-resolved.</param>
    member GetProjectOptionsFromCommandLineArgs : projectFileName: string * argv: string[] * ?loadedTimeStamp: DateTime * ?extraProjectInfo: obj -> FSharpProjectOptions

    /// <summary>
    /// <para>Get the FSharpParsingOptions implied by a FSharpProjectOptions.</para>
    /// </summary>
    ///
    /// <param name="argv">The command line arguments for the project build.</param>
    member GetParsingOptionsFromProjectOptions: FSharpProjectOptions -> FSharpParsingOptions * FSharpErrorInfo list

    /// Parse a file
    /// This is useful for operations like navigation that don't require a full typecheck
    /// TODO: implement resolve operation that resolves exported symbols without doing a full typecheck
    member Parse: options: FSharpParsingOptions * filename: string * source: string -> FSharpParseFileResults

    /// Open a file for checking
    /// An open file can be checked incrementally by specifying a "focus"
    member Open: options: FSharpProjectOptions * filename: string -> LSFile

    /// Check the entire file, regardless of what has been checked previously
    member CheckFully: options: FSharpProjectOptions * file: LSFile * source: string -> Async<FSharpParseFileResults * FSharpCheckFileAnswer>

    /// Check the expression around `focus`, and anything else that has been edited
    /// If the file has been extensively edited, this may fall back on `CheckFully`
    member CheckIncrementally: options: FSharpProjectOptions * file: LSFile * source: string * focus: range -> Async<FSharpParseFileResults * FSharpCheckFileAnswer>