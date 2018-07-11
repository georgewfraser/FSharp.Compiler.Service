The goal is to implement "focused compilation", where we avoid doing work that's not relevant to the region of code around the cursor.
The fundamental strategy is to skip things; there are several tricks:

1. When another .fs files has a corresponding .fsi file, we can skip the .fs file.
2. All expressions after the cursor can be skipped.
3. As we typecheck each expression, check if it is unchanged, and we can simply re-use the existing check.

The last part is the trickiest; it's essentially a form of memoization. 
If the contents of the file leading up to and including the next expression have not changed, we can return the last check result.
It's not necessary to implement this for all expressions; we only need to skip enough of the file that each compilation is fast.