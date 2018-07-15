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

    /// Parse a file
    /// This is useful for operations like navigation that don't require a full typecheck
    /// TODO: implement resolve operation that resolves exported symbols without doing a full typecheck
    member Parse: options: FSharpParsingOptions * filename: string * source: string -> FSharpParseFileResults

    /// Open a file for checking
    /// An open file can be checked incrementally by specifying a "focus"
    member Open: options: FSharpProjectOptions * filename: string -> LSFile

    /// Check the entire file, regardless of what has been checked previously
    member CheckFully: options: FSharpProjectOptions * file: LSFile * source: string -> Async<FSharpCheckFileAnswer>

    /// Check the expression around `focus`, and anything else that has been edited
    /// If the file has been extensively edited, this may fall back on `CheckFully`
    member CheckIncrementally: options: FSharpProjectOptions * file: LSFile * source: string * focus: pos -> Async<FSharpCheckFileAnswer>