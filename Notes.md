The goal is to implement "focused compilation", where we avoid doing work that's not relevant to the region of code around the cursor.
The fundamental strategy is to skip things; there are several tricks:

1. When another .fs files has a corresponding .fsi file, we can skip the .fs file.
2. All expressions after the cursor can be skipped.
3. Re-use the previous typecheck, up to the expression of interest

The last part is the trickiest. Implementation files can be divided into modules, let-declarations, and types:

```fsharp
module M1 = 
    let aLet = 1
    type aType = 
        member foo: unit -> string
module M2 = 
    let anotherLet = "2"
module M3 = 
    type anotherType = 
        member bar: unit -> string
```

For each `let` and `type`, the typechecker produces a list of new typed ASTs, a list of new attributes, and a new environment that's used to typecheck subsequent declarations.

```fsharp
module M1 = 
    let aLet = 1 
    // aLet
    type aType = 
        member foo: unit -> string
    // aLet, aType
// M1
module M2 = 
    let anotherLet = "2"
    // M1, anotherLet
// M1, M2
module M3 = 
    type anotherType = 
        member bar: unit -> string
    // M1, M2, anotherType
```

We save each of these environments and use it as a restart point. For example, if we want to re-typecheck `let anotherLet = "2"`, we simply recover the environment `M1` from the previous typecheck, and check `let anotherLet = "2"` by itself.