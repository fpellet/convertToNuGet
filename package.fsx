#r "packages/Mono.Cecil/lib/net40/Mono.Cecil.dll"
#r "packages/QuickGraph/lib/net4/QuickGraph.dll"
#r "packages/FAKE/tools/FakeLib.dll"
open Mono.Cecil
open System.IO
open QuickGraph
open System.Linq
open System.Reflection
open Fake

let downloadNuget (output: FileInfo) =
    use client = new System.Net.WebClient()

    client.DownloadFile("https://dist.nuget.org/win-x86-commandline/latest/nuget.exe", output.FullName)

let ifNotExist action (file: FileInfo) =
    if file.Exists |> not then action file

    file

let getNuget () =
    new FileInfo(Path.Combine(__SOURCE_DIRECTORY__, "nuget.exe"))
    |> ifNotExist downloadNuget

type AssemblyFile = { File: FileInfo; Name: string; FullName: string; Dependencies: AssemblyDependency list; Version: string; Author: string; Copyright: string }
and AssemblyDependency = 
    Assembly of AssemblyFileDependency
    | ExternalNuget of ExternalNuget
and ExternalNuget = { Name: string; Version: string }
and AssemblyFileDependency = { Name: string; FullName: string; Version: string }

let (|Prefix|_|) (p:string) (s:string) =
    if s.StartsWith(p) then
        Some(s.Substring(p.Length))
    else
        None

let excludeSystemAssemblies (assembly: AssemblyNameReference) =
    match assembly.Name with
    | "System.Windows.Interactivity" -> true
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
    | "WindowsBase" -> false
    | _ -> true

let convertToAssemblyDependency (assembly: AssemblyNameReference) =
    match assembly.Name with
    | "EntityFramework" -> ExternalNuget { Name = assembly.Name; Version = assembly.Version.ToString() }
    | _ -> Assembly { FullName = assembly.FullName; Name = assembly.Name; Version = assembly.Version.ToString() }

let extractAssemblyDependencies (assembly: AssemblyDefinition) =
    assembly.MainModule.AssemblyReferences 
    |> Seq.filter excludeSystemAssemblies
    |> Seq.map convertToAssemblyDependency

let convertToAssemblyFile (file: FileInfo) =
    let assembly = AssemblyDefinition.ReadAssembly(file.FullName)
    let versionInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(file.FullName)
    {
        File = file
        Name = assembly.Name.Name
        FullName = assembly.FullName
        Dependencies = extractAssemblyDependencies assembly |> Seq.toList
        Version = versionInfo.ProductVersion
        Copyright = versionInfo.LegalCopyright
        Author = versionInfo.CompanyName
    }

let searchAllAssemblies (folder: DirectoryInfo) =
    folder.GetFiles("*.dll") |> Seq.map convertToAssemblyFile

let addDependenciesInGraph addEdge (assembliesByFullName: System.Collections.Generic.IDictionary<string, AssemblyFile>) (assembly: AssemblyFile) =
    let getAssemblyByName dependency =
        match dependency with
        | ExternalNuget _ -> None
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
 
    templateFile.CopyTo(templateFileName) 

type NugetPackage = {
    TemplateFile: FileInfo
    Files: (string * string option * string option) list
    Dependencies: NugetDependencies
    Name: string
    Version: string
    Author: string
    Copyright: string
}

let createNugetPackage (nugetFile: FileInfo) (output: DirectoryInfo) (package: NugetPackage) =
    Fake.NuGetHelper.NuGet (
        fun p -> 
            {
            p with
                Files = package.Files
                OutputPath = output.FullName
                WorkingDir = output.FullName
                Dependencies = package.Dependencies
                ToolPath = nugetFile.FullName
                Description = package.Name
                Authors = [package.Author]
                Version = package.Version
                Project = package.Name
                Copyright = package.Copyright
            }
        ) (package.TemplateFile.FullName)

let convertToPackage (createTemplate: AssemblyFile -> FileInfo) (assembly: AssemblyFile) =
    let getDependencies (assembly: AssemblyFile) =
        assembly.Dependencies
        |> Seq.map (function | Assembly f -> f.Name, f.Version | ExternalNuget n -> n.Name, n.Version)

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
        Author = assembly.Author
        Copyright = assembly.Copyright
    }

let cleanOutput (output: DirectoryInfo) =
    if output.Exists then output.Delete(true)
    output.Create()

let createPackagesForDirectory inputFolder outputFolder =
    outputFolder |> cleanOutput

    let templateFile = FileInfo(Path.Combine(__SOURCE_DIRECTORY__, @"template.nuspec"))

    let createTemplate = createNugetTemplate templateFile outputFolder
    let createPackage = createNugetPackage (getNuget ()) outputFolder

    inputFolder
    |> searchAllAssemblies
    |> toGraph
    |> reverseDependencyOrder
    |> Seq.map (convertToPackage createTemplate)
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
let output = getBuildParamOrDefault "output" (Path.Combine(__SOURCE_DIRECTORY__, "nugetpackages")) |> DirectoryInfo

createPackagesForDirectory source output