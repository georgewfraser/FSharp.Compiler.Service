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

let checkFully(deleg: LSDelegate, projectOptions, sourceFile, source) = 
    match Async.RunSynchronously(deleg.CheckFully(projectOptions, sourceFile, source)) with 
    | _, FSharpCheckFileAnswer.Aborted -> failwith "Aborted"
    | parsed, FSharpCheckFileAnswer.Succeeded(check) -> parsed, check

let checkIncrementally(deleg: LSDelegate, projectOptions, sourceFile, source, focus) = 
    match Async.RunSynchronously(deleg.CheckIncrementally(projectOptions, sourceFile, source, focus)) with 
    | _, FSharpCheckFileAnswer.Aborted -> failwith "Aborted"
    | parsed, FSharpCheckFileAnswer.Succeeded(check) -> parsed, check

[<Test>]
let ``Test check file`` () = 
    let source = """
module Foo

let foo() = "Foo"
"""
    let deleg, sourceFileName, projectFileName, projectOptions, parsingOptions = singleFileProject(source)
    let sourceFile = deleg.Open(projectOptions, sourceFileName)
    let _, check = checkFully(deleg, projectOptions, sourceFile, source)
    let tooltip = Async.RunSynchronously(check.GetToolTipText(4, 8, "let foo() = \"Foo\"", ["foo"], FSharpTokenTag.Identifier))
    let asText = sprintf "%A" tooltip 
    StringAssert.Contains("val foo : unit -> string", asText)
    CollectionAssert.IsEmpty(check.Errors)

[<Test>]
let ``Test find error`` () = 
    let source = """
module Foo

let foo(): string = 1
"""
    let deleg, sourceFileName, projectFileName, projectOptions, parsingOptions = singleFileProject(source)
    let sourceFile = deleg.Open(projectOptions, sourceFileName)
    let _, check = checkFully(deleg, projectOptions, sourceFile, source)
    CollectionAssert.IsNotEmpty(check.Errors)

[<Test>]
let ``Test incremental completions`` () = 
    let source = """
module Foo

let outerFun() = 
    let x = 1
    x

module InnerModule = 
    let innerFun() = 
        let x = 1
        x
"""
    let deleg, sourceFileName, projectFileName, projectOptions, parsingOptions = singleFileProject(source)
    let sourceFile = deleg.Open(projectOptions, sourceFileName)
    let focus = Range.mkRange sourceFileName (Range.mkPos 6 4) (Range.mkPos 6 5)
    let parsed, check = checkIncrementally(deleg, projectOptions, sourceFile, source, focus)
    let completions = check.GetDeclarationListInfo(Some(parsed), 6, "    x", QuickParse.GetPartialLongNameEx("    x", 5)) |> Async.RunSynchronously
    let names = [for c in completions.Items -> c.Name]
    CollectionAssert.Contains(names, "x")
    CollectionAssert.IsEmpty(check.Errors)

