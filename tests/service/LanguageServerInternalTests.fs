module FSharp.Compiler.Service.Tests.LanguageServerInternalTests

open System.IO

open Microsoft.FSharp.Compiler
open Microsoft.FSharp.Compiler.LSP
open Microsoft.FSharp.Compiler.SourceCodeServices

open NUnit.Framework
open FSharp.Compiler.Service.Tests.Common

let singleFileProject(source: string) = 
    let deleg = LSDelegate()
    let sourceFileName = Path.ChangeExtension(Path.GetTempFileName(), ".fs")
    let projectFileName = Path.ChangeExtension(Path.GetTempFileName(), ".fsproj")
    let dllName = Path.ChangeExtension(projectFileName, ".dll")
    File.WriteAllText(sourceFileName, source)
    let args = mkProjectCommandLineArgs(dllName, [sourceFileName])
    let projectOptions = deleg.GetProjectOptionsFromCommandLineArgs(projectFileName, args)
    let parsingOptions, _ = deleg.GetParsingOptionsFromProjectOptions(projectOptions)
    deleg, sourceFileName, projectFileName, projectOptions, parsingOptions

[<Test>]
let ``Test check file`` () = 
    let source = """
module Foo

let foo() = "Foo"
"""
    let deleg, sourceFileName, projectFileName, projectOptions, parsingOptions = singleFileProject(source)
    let sourceFile = deleg.Open(projectOptions, sourceFileName)
    match Async.RunSynchronously(deleg.CheckFully(projectOptions, sourceFile, source)) with 
    | FSharpCheckFileAnswer.Aborted -> Assert.Fail("Aborted")
    | FSharpCheckFileAnswer.Succeeded(results) -> 
        let tooltip = Async.RunSynchronously(results.GetToolTipText(4, 8, "let foo() = \"Foo\"", ["foo"], FSharpTokenTag.Identifier))
        let asText = sprintf "%A" tooltip 
        StringAssert.Contains("val foo : unit -> string", asText)

