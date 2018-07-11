dotnet test fcs/FSharp.Compiler.Service.Tests/FSharp.Compiler.Service.Tests.fsproj \
    -c Release \
    --filter 'FullyQualifiedName=FSharp.Compiler.Service.Tests.PerfTests.Test request for parse and check focuses on a single declaration'