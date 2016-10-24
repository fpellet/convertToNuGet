#r "packages/Mono.Cecil/lib/net40/Mono.Cecil.dll"
#r "packages/QuickGraph/lib/net4/QuickGraph.dll"
#r "packages/FAKE/tools/FakeLib.dll"
open Mono.Cecil
open System.IO
open QuickGraph
open System.Linq
open System.Reflection
open Fake

let folder = @"C:\testing\DevExpress 15.2\Components\Bin\Framework"

let baseName = "DevExpress."

let downloadNuget () =
    use client = new System.Net.WebClient()

    client.DownloadFile("https://dist.nuget.org/win-x86-commandline/latest/nuget.exe", "./nuget.exe")

let downloadNugetIfNotExist () =
    if File.Exists("./nuget.exe") |> not then downloadNuget ()

let getTopology () =
  let graph = new QuickGraph.AdjacencyGraph<AssemblyDefinition, IEdge<AssemblyDefinition>>()

  let filenameByAssembly =
    let dir = new DirectoryInfo(folder)
    let files = dir.GetFiles(baseName + "*.dll")
    files
    |> Seq.map (fun f -> AssemblyDefinition.ReadAssembly(f.FullName), f.FullName)
    |> dict

  let assemblyDefinitions = 
    filenameByAssembly.Keys
    |> Seq.map (fun a -> a.Name.FullName, a)
    |> dict

  assemblyDefinitions.Values
  |> graph.AddVertexRange
  |> ignore

  for a in assemblyDefinitions.Values do
    for r in a.MainModule.AssemblyReferences do
      if r.FullName.StartsWith baseName then
        if assemblyDefinitions.ContainsKey(r.FullName) |> not then trace ("Missing " + r.FullName)

        if assemblyDefinitions.ContainsKey(r.FullName) then
            graph.AddEdge(new Edge<_>(a, assemblyDefinitions.[r.FullName])) |> ignore
  
  graph, filenameByAssembly

let assembliesInReverseDependencyOrder (graph) =
  let a = new Algorithms.TopologicalSort.TopologicalSortAlgorithm<_,_>(graph)
  a.Compute()
  a.SortedVertices
  |> Seq.rev
  |> Seq.toArray

open Mono.Cecil

let getVersion (assembly: AssemblyDefinition) =
  let versionAttributeTypeName = typeof<AssemblyFileVersionAttribute>.FullName
  match assembly.CustomAttributes.FirstOrDefault(fun f ->f.AttributeType.FullName = versionAttributeTypeName) with
  | null -> None
  | a -> Some (a.ConstructorArguments.First().Value :?> string)

let writePackages () =
  let graph, filenames = getTopology ()
  let assemblies = assembliesInReverseDependencyOrder (graph)
  let outputDir = Path.Combine(__SOURCE_DIRECTORY__, "nugetpackages", baseName)
  let dir = DirectoryInfo(outputDir)
  if dir.Exists then
    dir.Delete(true)
  let asyncs = seq {
    for a in assemblies do
    
      match filenames.TryGetValue(a) with
      | false, _ -> printfn "no filename for it???"
      | true, filename -> printfn "filename %s" filename
    
      let version = getVersion a
      if version.IsSome then
        let deps = 
          match graph.TryGetOutEdges(a) with
          | false, _ -> []
          | true, deps ->
            deps
            |> Seq.map (fun e -> e.Target.Name.Name, (getVersion e.Target).Value)
            |> Seq.toList
        ()
        
        Directory.CreateDirectory(outputDir)
        let fileNames = [
          let assemblyFile = filenames.[a]
          let docFile = assemblyFile.Replace(".dll", ".xml")
          yield (assemblyFile, Some "lib/net40/", None)
          if File.Exists docFile then
            yield (docFile, Some "lib/net40", None)
        ]
        let template = FileInfo(Path.Combine(__SOURCE_DIRECTORY__, @"template.devexpress.nuspec"))
        let newTemplate =
          Path.Combine(outputDir, a.Name.Name + "." + template.Name)

        template.CopyTo(newTemplate)
      
        yield (async {
          Fake.NuGetHelper.NuGet (
            fun p -> 
              {
                p with
                  Files = fileNames
                  OutputPath = outputDir
                  WorkingDir = outputDir
                  Dependencies = deps
                  ToolPath = Path.Combine(__SOURCE_DIRECTORY__, "nuget.exe")
                  Description = a.Name.Name
                  Authors = ["DevExpress"]
                  Version = version.Value
                  Project = a.Name.Name
              }
          ) newTemplate
          return ()
        })
  }

  Async.Parallel asyncs
  |> Async.RunSynchronously

downloadNugetIfNotExist()
writePackages()