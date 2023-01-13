﻿// Copyright (c) Microsoft Corporation.  All Rights Reserved.  See License.txt in the project root for license information.

namespace FSharp.Test

open System
open System.IO
open System.Reflection
open System.Collections.Immutable
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open TestFramework
open NUnit.Framework

type TheoryForNETCOREAPPAttribute() = 
    inherit Xunit.TheoryAttribute()
    #if !NETCOREAPP    
        do base.Skip <- "Only NETCOREAPP is supported runtime for this kind of test."
    #endif

type FactForNETCOREAPPAttribute() =
    inherit Xunit.FactAttribute()
    #if !NETCOREAPP    
        do base.Skip <- "Only NETCOREAPP is supported runtime for this kind of test."
    #endif

type FactForDESKTOPAttribute() =
    inherit Xunit.FactAttribute()
    #if NETCOREAPP
        do base.Skip <- "NETCOREAPP is not supported runtime for this kind of test, it is intended for DESKTOP only"
    #endif

// This file mimics how Roslyn handles their compilation references for compilation testing
module Utilities =

    type Async with
        static member RunImmediate (computation: Async<'T>, ?cancellationToken ) =
            let cancellationToken = defaultArg cancellationToken Async.DefaultCancellationToken
            let ts = TaskCompletionSource<'T>()
            let task = ts.Task
            Async.StartWithContinuations(
                computation,
                (fun k -> ts.SetResult k),
                (fun exn -> ts.SetException exn),
                (fun _ -> ts.SetCanceled()),
                cancellationToken)
            task.Result

    /// Disposable type to implement a simple resolve handler that searches the currently loaded assemblies to see if the requested assembly is already loaded.
    type AlreadyLoadedAppDomainResolver () =
        let resolveHandler =
            ResolveEventHandler(fun _ args ->
                let assemblies = AppDomain.CurrentDomain.GetAssemblies()
                let assembly = assemblies |> Array.tryFind(fun a -> String.Compare(a.FullName, args.Name,StringComparison.OrdinalIgnoreCase) = 0)
                assembly |> Option.defaultValue Unchecked.defaultof<Assembly>
                )
        do AppDomain.CurrentDomain.add_AssemblyResolve(resolveHandler)

        interface IDisposable with
            member this.Dispose() = AppDomain.CurrentDomain.remove_AssemblyResolve(resolveHandler)


    [<RequireQualifiedAccess>]
    type TargetFramework =
        | NetStandard20
        | NetCoreApp31
        | Current

    let private getResourceStream name =
        let assembly = typeof<TargetFramework>.GetTypeInfo().Assembly

        let stream = assembly.GetManifestResourceStream(name);

        match stream with
        | null -> failwith (sprintf "Resource '%s' not found in %s." name assembly.FullName)
        | _ -> stream

    let private getResourceBlob name =
        use stream = getResourceStream name
        let (bytes: byte[]) = Array.zeroCreate (int stream.Length)
        use memoryStream = new MemoryStream (bytes)
        stream.CopyTo(memoryStream)
        bytes

    let inline getTestsDirectory src dir = src ++ dir

    let private getOrCreateResource (resource: byref<byte[]>) (name: string) =
        match resource with
        | null -> getResourceBlob name
        | _ -> resource

    module private TestReferences =
        [<RequireQualifiedAccess>]
        module NetStandard20 =
            let netStandard = lazy AssemblyMetadata.CreateFromImage(TestResources.NetFX.netstandard20.netstandard).GetReference(display = "netstandard.dll (netstandard 2.0 ref)")
            let mscorlibRef = lazy AssemblyMetadata.CreateFromImage(TestResources.NetFX.netstandard20.mscorlib).GetReference(display = "mscorlib.dll (netstandard 2.0 ref)")
            let systemRuntimeRef = lazy AssemblyMetadata.CreateFromImage(TestResources.NetFX.netstandard20.System_Runtime).GetReference(display = "System.Runtime.dll (netstandard 2.0 ref)")
            let systemCoreRef = lazy AssemblyMetadata.CreateFromImage(TestResources.NetFX.netstandard20.System_Core).GetReference(display = "System.Core.dll (netstandard 2.0 ref)")
            let systemDynamicRuntimeRef = lazy AssemblyMetadata.CreateFromImage(TestResources.NetFX.netstandard20.System_Dynamic_Runtime).GetReference(display = "System.Dynamic.Runtime.dll (netstandard 2.0 ref)")


        module private NetCoreApp31Refs =
            let mutable (_mscorlib: byte[]) = Unchecked.defaultof<byte[]>
            let mutable (_netstandard: byte[]) = Unchecked.defaultof<byte[]>
            let mutable (_System_Console: byte[]) = Unchecked.defaultof<byte[]>
            let mutable (_System_Core: byte[]) = Unchecked.defaultof<byte[]>
            let mutable (_System_Dynamic_Runtime: byte[]) = Unchecked.defaultof<byte[]>
            let mutable (_System_Runtime: byte[]) = Unchecked.defaultof<byte[]>
            let mscorlib () = getOrCreateResource &_mscorlib "mscorlib.dll"
            let netstandard () = getOrCreateResource &_netstandard "netstandard.dll"
            let System_Core () = getOrCreateResource &_System_Core "System.Core.dll"
            let System_Console () = getOrCreateResource &_System_Console "System.Console.dll"
            let System_Runtime () = getOrCreateResource &_System_Runtime "System.Runtime.dll"
            let System_Dynamic_Runtime () = getOrCreateResource &_System_Dynamic_Runtime "System.Dynamic.Runtime.dll"

        [<RequireQualifiedAccess>]
        module NetCoreApp31 =
            let netStandard = lazy AssemblyMetadata.CreateFromImage(NetCoreApp31Refs.netstandard ()).GetReference(display = "netstandard.dll (netcoreapp 3.1 ref)")
            let mscorlibRef = lazy AssemblyMetadata.CreateFromImage(NetCoreApp31Refs.mscorlib ()).GetReference(display = "mscorlib.dll (netcoreapp 3.1 ref)")
            let systemRuntimeRef = lazy AssemblyMetadata.CreateFromImage(NetCoreApp31Refs.System_Runtime ()).GetReference(display = "System.Runtime.dll (netcoreapp 3.1 ref)")
            let systemCoreRef = lazy AssemblyMetadata.CreateFromImage(NetCoreApp31Refs.System_Core ()).GetReference(display = "System.Core.dll (netcoreapp 3.1 ref)")
            let systemDynamicRuntimeRef = lazy AssemblyMetadata.CreateFromImage(NetCoreApp31Refs.System_Dynamic_Runtime ()).GetReference(display = "System.Dynamic.Runtime.dll (netcoreapp 3.1 ref)")
            let systemConsoleRef = lazy AssemblyMetadata.CreateFromImage(NetCoreApp31Refs.System_Console ()).GetReference(display = "System.Console.dll (netcoreapp 3.1 ref)")

    [<RequireQualifiedAccess>]
    module public TargetFrameworkUtil =

        let private config = TestFramework.initializeSuite ()

        // Do a one time dotnet sdk build to compute the proper set of reference assemblies to pass to the compiler
        let private projectFile = """
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
        <OutputType>Exe</OutputType>
        <TargetFramework>$TARGETFRAMEWORK</TargetFramework>
        <UseFSharpPreview>true</UseFSharpPreview>
        <DisableImplicitFSharpCoreReference>true</DisableImplicitFSharpCoreReference>
  </PropertyGroup>

  <ItemGroup><Compile Include="Program.fs" /></ItemGroup>
  <ItemGroup><Reference Include="$FSHARPCORELOCATION" /></ItemGroup>
  <ItemGroup Condition="'$(TARGETFRAMEWORK)'=='net472'">
        <Reference Include="System" />
        <Reference Include="System.Runtime" />
        <Reference Include="System.Core.dll" />
        <Reference Include="System.Xml.Linq.dll" />
        <Reference Include="System.Data.DataSetExtensions.dll" />
        <Reference Include="Microsoft.CSharp.dll" />
        <Reference Include="System.Data.dll" />
        <Reference Include="System.Deployment.dll" />
        <Reference Include="System.Drawing.dll" />
        <Reference Include="System.Net.Http.dll" />
        <Reference Include="System.Windows.Forms.dll" />
        <Reference Include="System.Xml.dll" />
  </ItemGroup>

  <Target Name="WriteFrameworkReferences" AfterTargets="AfterBuild">
        <WriteLinesToFile File="FrameworkReferences.txt" Lines="@(ReferencePath)" Overwrite="true" WriteOnlyWhenDifferent="true" />
  </Target>

</Project>"""

        let private directoryBuildProps = """
<Project>
</Project>
"""

        let private directoryBuildTargets = """
<Project>
</Project>
"""

        let private programFs = """
open System

[<EntryPoint>]
let main argv = 0"""

        let private getNetCoreAppReferences =
            let mutable output = ""
            let mutable errors = ""
            let mutable cleanUp = true
            let pathToArtifacts = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "../../../.."))
            if Path.GetFileName(pathToArtifacts) <> "artifacts" then failwith "CompilerAssert did not find artifacts directory --- has the location changed????"
            let pathToTemp = Path.Combine(pathToArtifacts, "Temp")
            let projectDirectory = Path.Combine(pathToTemp,Guid.NewGuid().ToString() + ".tmp")
            let pathToFSharpCore = typeof<RequireQualifiedAccessAttribute>.Assembly.Location
            try
                try
                    Directory.CreateDirectory(projectDirectory) |> ignore
                    let projectFileName = Path.Combine(projectDirectory, "ProjectFile.fsproj")
                    let programFsFileName = Path.Combine(projectDirectory, "Program.fs")
                    let directoryBuildPropsFileName = Path.Combine(projectDirectory, "Directory.Build.props")
                    let directoryBuildTargetsFileName = Path.Combine(projectDirectory, "Directory.Build.targets")
                    let frameworkReferencesFileName = Path.Combine(projectDirectory, "FrameworkReferences.txt")
#if NETCOREAPP
                    File.WriteAllText(projectFileName, projectFile.Replace("$TARGETFRAMEWORK", "net7.0").Replace("$FSHARPCORELOCATION", pathToFSharpCore))
#else
                    File.WriteAllText(projectFileName, projectFile.Replace("$TARGETFRAMEWORK", "net472").Replace("$FSHARPCORELOCATION", pathToFSharpCore))
#endif
                    File.WriteAllText(programFsFileName, programFs)
                    File.WriteAllText(directoryBuildPropsFileName, directoryBuildProps)
                    File.WriteAllText(directoryBuildTargetsFileName, directoryBuildTargets)

                    let timeout = 30000
                    let exitCode, output, errors = Commands.executeProcess (Some config.DotNetExe) "build" projectDirectory timeout

                    if exitCode <> 0 || errors.Length > 0 then
                        printfn "Output:\n=======\n"
                        output |> Seq.iter(fun line -> printfn "STDOUT:%s\n" line)
                        printfn "Errors:\n=======\n"
                        errors  |> Seq.iter(fun line -> printfn "STDERR:%s\n" line)
                        Assert.True(false, "Errors produced generating References")

                    File.ReadLines(frameworkReferencesFileName) |> Seq.toArray
                with | e ->
                    cleanUp <- false
                    printfn "Project directory: %s" projectDirectory
                    printfn "STDOUT: %s" output
                    File.WriteAllText(Path.Combine(projectDirectory, "project.stdout"), output)
                    printfn "STDERR: %s" errors
                    File.WriteAllText(Path.Combine(projectDirectory, "project.stderror"), errors)
                    raise (new Exception (sprintf "An error occurred getting netcoreapp references: %A" e))
            finally
                if cleanUp then
                    try Directory.Delete(projectDirectory, recursive=true) with | _ -> ()

        open TestReferences

        let private netStandard20References =
            lazy ImmutableArray.Create(NetStandard20.netStandard.Value, NetStandard20.mscorlibRef.Value, NetStandard20.systemRuntimeRef.Value, NetStandard20.systemCoreRef.Value, NetStandard20.systemDynamicRuntimeRef.Value)
        let private netCoreApp31References =
            lazy ImmutableArray.Create(NetCoreApp31.netStandard.Value, NetCoreApp31.mscorlibRef.Value, NetCoreApp31.systemRuntimeRef.Value, NetCoreApp31.systemCoreRef.Value, NetCoreApp31.systemDynamicRuntimeRef.Value, NetCoreApp31.systemConsoleRef.Value)

        let currentReferences =
            getNetCoreAppReferences

        let currentReferencesAsPEs =
            getNetCoreAppReferences
            |> Seq.map (fun x ->
                PortableExecutableReference.CreateFromFile(x)
            )
            |> ImmutableArray.CreateRange

        let getReferences tf =
            match tf with
                | TargetFramework.NetStandard20 -> netStandard20References.Value
                | TargetFramework.NetCoreApp31 -> netCoreApp31References.Value
                | TargetFramework.Current -> currentReferencesAsPEs
