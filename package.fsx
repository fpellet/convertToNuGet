#r "packages/Mono.Cecil/lib/net40/Mono.Cecil.dll"
#r "packages/QuickGraph/lib/net4/QuickGraph.dll"
#r "packages/FAKE/tools/FakeLib.dll"
#r "packages/NuGet.Core/lib/net40-Client/NuGet.Core.dll"
#r "System.Xml.Linq"
open Mono.Cecil
open System.IO
open QuickGraph
open System.Linq
open System.Reflection
open NuGet
open Fake

let output = getBuildParamOrDefault "output" (Path.Combine(__SOURCE_DIRECTORY__, "nugetpackages")) |> DirectoryInfo
let publishUrl = getBuildParamOrDefault "publishUrl" ""
let nugetSource = getBuildParamOrDefault "nugetSource" output.FullName

let getNuget () =
    new FileInfo(Path.Combine(__SOURCE_DIRECTORY__, "packages/NuGet.CommandLine/tools", "NuGet.exe"))

type AssemblyFile = { File: FileInfo; Name: string; FullName: string; Dependencies: AssemblyDependency list; Version: string; Author: string; Copyright: string }
and AssemblyDependency = 
    Assembly of AssemblyFileDependency
    | ExternalNuget of ExternalNuget
    | FrameworkAssembly of FrameworkAssembly
and ExternalNuget = { Name: string; Version: string }
and AssemblyFileDependency = { Name: string; FullName: string; Version: string }
and FrameworkAssembly = { Name: string; Version: string }

let checkIfNugetPackageExists' (name: string, version: string) source =
    let repo = PackageRepositoryFactory.Default.CreateRepository(source)
    
    repo.FindPackage(name, SemanticVersion.Parse(version)) <> null

let nugetSources = [ nugetSource; "https://packages.nuget.org/api/v2" ]

let normalizeNugetPackage (name: string, version: string) =
    match (name, version) with
    | "dotless.Core", v -> "dotless", v
    | "Newtonsoft.Json", "8.0.0.0" -> "Newtonsoft.Json", "8.0.1"
    | "log4net", "1.2.10" -> "log4net", "1.2.10"
    | "log4net", v -> "log4net", v.Replace("1.2.1", "2.0.")
    | n, v -> n, v

let checkIfNugetPackageExists (name: string) (version: string) =
    let package = normalizeNugetPackage (name, version)

    tracef "check nuget %A" package

    let exists = nugetSources |> Seq.exists (checkIfNugetPackageExists' package)

    tracefn " -> %A" exists

    if exists then Some <| ExternalNuget { Name = fst package; Version = snd package }
    else None

let memoize f =
    let cache = new System.Collections.Generic.Dictionary<_, _>()

    (fun x y ->
        match cache.TryGetValue((x, y)) with
        | true, cachedValue -> cachedValue
        | _ -> 
            let result = f x y
            cache.Add((x, y), result)
            result)

let checkIfNugetPackageExistsWithCache = memoize checkIfNugetPackageExists

let (|Prefix|_|) (p:string) (s:string) =
    if s.StartsWith(p) then
        Some(s.Substring(p.Length))
    else
        None

let isFrameworkAssemblies (assembly: AssemblyNameReference) =
    match assembly.Name with
    | "System.Windows.Interactivity" -> false
    | Prefix "System" _
    | Prefix "Microsoft" _
    | "mscorlib"
    | "PresentationCore"
    | "PresentationFramework"
    | "UIAutomationClient"
    | "UIAutomationTypes"
    | "UIAutomationProvider"
    | "ReachFramework"
    | "WindowsFormsIntegration"
    | "WindowsBase"
    | "Windows.Foundation.UniversalApiContract"
    | "Windows.Foundation.FoundationContract" -> true
    | _ -> false

let convertToAssemblyDependency (assembly: AssemblyNameReference) =
    let name = assembly.Name
    let version = assembly.Version.ToString()

    if isFrameworkAssemblies assembly then FrameworkAssembly { Name = name; Version = version }
    else
        let maybeNuget = checkIfNugetPackageExistsWithCache name version
        if maybeNuget.IsSome then maybeNuget.Value
        else Assembly { FullName = assembly.FullName; Name = name; Version = version }

let extractAssemblyDependencies (assembly: AssemblyDefinition) =
    assembly.MainModule.AssemblyReferences 
    |> Seq.map convertToAssemblyDependency

let orUnknownIfEmpty value =
    if System.String.IsNullOrWhiteSpace(value) 
        then "Unknown" 
        else value

let convertToAssemblyFile (file: FileInfo) =
    let assembly = AssemblyDefinition.ReadAssembly(file.FullName)
    let versionInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(file.FullName)
    {
        File = file
        Name = assembly.Name.Name
        FullName = assembly.FullName
        Dependencies = extractAssemblyDependencies assembly |> Seq.toList
        Version = assembly.Name.Version.ToString()
        Copyright = versionInfo.LegalCopyright
        Author = versionInfo.CompanyName |> orUnknownIfEmpty
    }

let searchAllAssemblies (folder: DirectoryInfo) =
    folder.GetFiles("*.dll") |> Seq.map convertToAssemblyFile

let addDependenciesInGraph addEdge (assembliesByFullName: System.Collections.Generic.IDictionary<string, AssemblyFile>) (assembly: AssemblyFile) =
    let getAssemblyByName dependency =
        match dependency with
        | FrameworkAssembly _
        | ExternalNuget _ -> None
        | Assembly a when a.FullName = "PIA.SpMikeCtrl, Version=2.7.230.27, Culture=neutral, PublicKeyToken=a7078f88e8db8d11" ->
            let key = assembliesByFullName |> Seq.find (fun a -> a.Key.StartsWith("PIA.SpMikeCtrl, Version=2.8"))
            Some key.Value
        | Assembly a ->
            if assembliesByFullName.ContainsKey(a.FullName) |> not
            then traceError ("Missing dependency " + a.FullName)
        
            Some assembliesByFullName.[a.FullName]

    assembly.Dependencies 
    |> Seq.map getAssemblyByName
    |> Seq.filter Option.isSome
    |> Seq.map Option.get
    |> Seq.iter (fun d -> addEdge(assembly, d))

let toGraph (assemblies: AssemblyFile seq) =
    let graph = new QuickGraph.AdjacencyGraph<AssemblyFile, IEdge<AssemblyFile>>()
    
    let assembliesByFullName = assemblies |> Seq.map (fun a -> a.FullName, a) |> dict

    assembliesByFullName.Values |> graph.AddVertexRange |> ignore

    assembliesByFullName.Values |> Seq.iter (addDependenciesInGraph (fun d -> graph.AddEdge(new Edge<_>(d)) |> ignore) assembliesByFullName)

    graph

let reverseDependencyOrder graph =
  let a = new Algorithms.TopologicalSort.TopologicalSortAlgorithm<_,_>(graph)
  a.Compute()
  a.SortedVertices
  |> Seq.rev

let createNugetTemplate (templateFile: FileInfo) (output: DirectoryInfo) (assembly: AssemblyFile) : FileInfo =
    let templateFileName = Path.Combine(output.FullName, assembly.Name + "." + templateFile.Name) 
 
    templateFile.CopyTo(templateFileName, true) 

type NugetPackage = {
    TemplateFile: FileInfo
    Files: (string * string option * string option) list
    Dependencies: NugetDependencies
    FrameworkAssemblies: NugetFrameworkAssemblyReferences list
    Name: string
    Version: string
    Author: string
    Copyright: string
    Language: string
}

let createNugetPackage (nugetFile: FileInfo) (output: DirectoryInfo) (publishUrl: string) (package: NugetPackage) =
    
    processTemplates ["@language@", package.Language] [ package.TemplateFile.FullName ]

    Fake.NuGetHelper.NuGet (
        fun p -> 
            {
            p with
                Files = package.Files
                OutputPath = output.FullName
                WorkingDir = output.FullName
                Dependencies = package.Dependencies
                FrameworkAssemblies = package.FrameworkAssemblies
                ToolPath = nugetFile.FullName
                Description = package.Name
                Authors = [package.Author]
                Version = package.Version
                Project = package.Name
                Copyright = package.Copyright
                Publish = System.String.IsNullOrWhiteSpace(publishUrl) |> not
                PublishUrl = publishUrl
            }
        ) (package.TemplateFile.FullName)

let convertToNugetPackage (createTemplate: AssemblyFile -> FileInfo) (assembly: AssemblyFile) : NugetPackage =
    let getDependencies (assembly: AssemblyFile) =
        assembly.Dependencies
        |> Seq.map (function | Assembly f -> Some (f.Name, f.Version) | ExternalNuget n -> Some(n.Name, n.Version) | FrameworkAssembly _ -> None)
        |> Seq.filter Option.isSome
        |> Seq.map Option.get

    let getFrameworkAssemblies (assembly: AssemblyFile) =
        assembly.Dependencies
        |> Seq.map (function | FrameworkAssembly f -> Some { FrameworkVersions = []; AssemblyName = f.Name } | Assembly _ | ExternalNuget _ -> None)
        |> Seq.filter Option.isSome
        |> Seq.map Option.get

    let getFiles assembly =
        seq {
            let assemblyFile = assembly.File.FullName
            yield (assemblyFile, Some "lib/net40/", None)
            
            let docFile = assemblyFile.Replace(".dll", ".xml")
            if File.Exists docFile 
            then yield (docFile, Some "lib/net40", None)
        }

    {
        TemplateFile = createTemplate assembly
        Name = assembly.Name
        Version = assembly.Version
        Files = getFiles assembly |> Seq.toList
        Dependencies = getDependencies assembly |> Seq.toList
        FrameworkAssemblies = getFrameworkAssemblies assembly |> Seq.toList
        Author = assembly.Author |> orUnknownIfEmpty
        Copyright = assembly.Copyright
        Language = ""
    }

let convertToNugetPackageWithCulture culture version convertToPackage (assembly: AssemblyFile) : NugetPackage =
    let addCultureFolder files =
        files 
        |> Seq.map (fun (file, directory, exclude) -> (file, directory |> Option.bind (fun v -> Path.Combine(v, culture) |> Some), exclude))
        |> Seq.toList

    let addPackageBase () =
        (assembly.Name.Substring(0, assembly.Name.Length - ".resources".Length), RequireExactly version)

    let package = convertToPackage assembly

    match assembly.Name with
    | name when name.EndsWith(".resources") -> 
            {
                package with
                    Files = (assembly.File.FullName, Some "Content", None) :: package.Files |> addCultureFolder
                    Dependencies = addPackageBase () :: package.Dependencies
                    Version = version
                    Name = assembly.Name.Replace(".resources", "." + culture)
                    Language = culture
            }
    | _ -> package

let createIfNotExistsOutput (output: DirectoryInfo) =
    if output.Exists |> not then output.Create()

let createPackagesForDirectory inputFolder outputFolder publishUrl culture cultureVersion =
    outputFolder |> createIfNotExistsOutput

    let templateFile = FileInfo(Path.Combine(__SOURCE_DIRECTORY__, @"template.nuspec"))

    let createTemplate = createNugetTemplate templateFile outputFolder
    let createPackage = createNugetPackage (getNuget ()) outputFolder publishUrl
    let convertToPackage = 
        if System.String.IsNullOrWhiteSpace(culture) then convertToNugetPackage createTemplate
        else convertToNugetPackageWithCulture culture cultureVersion (convertToNugetPackage createTemplate)

    inputFolder
    |> searchAllAssemblies
    |> Seq.filter (fun a -> checkIfNugetPackageExistsWithCache a.Name a.Version |> Option.isNone)
    |> toGraph
    |> reverseDependencyOrder
    |> Seq.map convertToPackage
    |> Seq.map (fun p -> logfn "%A" p; p)
    |> Seq.iter createPackage

let rec askSource () =
    let value = getUserInput "Directory with dlls ? " |> DirectoryInfo
    if value.Exists |> not then askSource ()
    else value

let source = 
    try
        let value = getBuildParamOrDefault "source" ""
        if System.String.IsNullOrWhiteSpace(value) then askSource ()
        else value |> DirectoryInfo
    with _ -> askSource ()

let culture = getBuildParamOrDefault "culture" ""
let cultureVersion = getBuildParamOrDefault "cultureVersion" ""

createPackagesForDirectory source output publishUrl culture cultureVersion