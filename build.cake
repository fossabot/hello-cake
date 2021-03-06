/*
 * Install addins.
 */
#addin "nuget:?package=Cake.Coverlet&version=2.2.1"
#addin "nuget:?package=Cake.Json&version=3.0.1"
#addin "nuget:?package=Newtonsoft.Json&version=9.0.1"

/*
 * Install tools.
 */
#tool "nuget:?package=GitReleaseNotes&version=0.7.1"
#tool "nuget:?package=GitVersion.CommandLine&version=4.0.0"
#tool "nuget:?package=ReportGenerator&version=4.0.13"
#tool "nuget:?package=xunit.runner.console&version=2.4.1"

/*
 * Load other scripts.
 */
#load "./build/parameters.cake"
#load "./build/utils.cake"

/*
 * Variables
 */
bool publishingError = false;

/*
 * Setup
 */
Setup<BuildParameters>(context =>
{
    var parameters = BuildParameters.GetParameters(Context);
    var gitVersion = GetVersion(parameters);
    parameters.Initialize(context, gitVersion, 1);

    if (parameters.IsMainBranch && (context.Log.Verbosity != Verbosity.Diagnostic)) {
        Information("Increasing verbosity to diagnostic.");
        context.Log.Verbosity = Verbosity.Diagnostic;
    }

    Information("Building of Hello Cake ({0}) with dotnet version {1}", parameters.Configuration, GetDotnetVersion());

    Information("Build version : Version {0}, SemVersion {1}, NuGetVersion: {2}",
        parameters.Version.Version, parameters.Version.SemVersion, parameters.Version.NuGetVersion);

    Information("Repository info : IsMainRepo {0}, IsMainBranch {1}, IsTagged: {2}, IsPullRequest: {3}",
        parameters.IsMainRepo, parameters.IsMainBranch, parameters.IsTagged, parameters.IsPullRequest);

    return parameters;
});

/*
 * Teardown
 */
Teardown<BuildParameters>((context, parameters) =>
{
    if(context.Successful)
    {
        // if(parameters.ShouldPublish)
        // {
        //     if(parameters.CanPostToGitter)
        //     {
        //         var message = "@/all Version " + parameters.Version.SemVersion + " of the GitVersion has just been released, https://www.nuget.org/packages/GitVersion.";

        //         var postMessageResult = Gitter.Chat.PostMessage(
        //             message: message,
        //             messageSettings: new GitterChatMessageSettings { Token = parameters.Gitter.Token, RoomId = parameters.Gitter.RoomId}
        //         );

        //         if (postMessageResult.Ok)
        //         {
        //             Information("Message {0} succcessfully sent", postMessageResult.TimeStamp);
        //         }
        //         else
        //         {
        //             Error("Failed to send message: {0}", postMessageResult.Error);
        //         }
        //     }
        // }
        Information("Finished running tasks. Thanks for your patience :D");
    }
    else
    {
        Error("What a fuck! :|");
        Error(context.ThrownException.Message);
    }
});

/*
 * Tasks
 */
Task("Clean")   
    .Does<BuildParameters>((parameters) => 
    {
        CleanDirectories(parameters.Paths.Directories.ToClean);

        CleanDirectories($"./**/bin/{parameters.Configuration}");
        CleanDirectories("./**/obj");
    });

Task("Build")
    .IsDependentOn("Clean")
    .Does<BuildParameters>((parameters) =>
    {
        foreach (var project in Ge)
        {
            Build(project, parameters.Configuration, parameters.MSBuildSettings);        
        }
    });

Task("Test")
    .WithCriteria<BuildParameters>((context, parameters) => parameters.EnabledUnitTests, "Unit tests were disabled.")
    .IsDependentOn("Build")
    .Does<BuildParameters>((parameters) => 
    {
        var settings = new DotNetCoreTestSettings 
        {
            Configuration = parameters.Configuration
        };

        var timestamp = $"{DateTime.UtcNow:dd-MM-yyyy-HH-mm-ss-FFF}";

        var coverletSettings = new CoverletSettings 
        {
            CollectCoverage = true,
            CoverletOutputDirectory = parameters.Paths.Directories.TestCoverageOutput,
            CoverletOutputName = $"results.{timestamp}.xml",
            Exclude = new List<string>() { "[xunit.*]*", "[Omnichannel.Connector.Proxies*]*" }
        };

        var projects = GetFiles("./**/*.Spec.csproj");

        if (projects.Count > 1)
            coverletSettings.MergeWithFile = $"{coverletSettings.CoverletOutputDirectory.FullPath}/{coverletSettings.CoverletOutputName}";

        var i = 1;
        foreach (var project in projects)
        {   
            if (i++ == projects.Count)
                coverletSettings.CoverletOutputFormat = CoverletOutputFormat.cobertura;

            var projectName = project.GetFilenameWithoutExtension();
            Information("Run specs for {0}", projectName);

            settings.ArgumentCustomization = args => args
                .Append("--logger").AppendQuoted($"trx;LogFileName={MakeAbsolute(parameters.Paths.Directories.TestResultOutput).FullPath}/{projectName}_{timestamp}.trx");

            DotNetCoreTest(project.FullPath, settings, coverletSettings);
        }
    });

Task("Coverage-Report")
    .Does<BuildParameters>((parameters) => 
    {
        var settings = new ReportGeneratorSettings
        {
            ReportTypes = { ReportGeneratorReportType.HtmlInline }
        };

        if (parameters.IsRunningOnAzurePipeline)
            settings.ReportTypes.Add(ReportGeneratorReportType.HtmlInline_AzurePipelines);

        ReportGenerator(GetFiles($"{parameters.Paths.Directories.TestCoverageOutput.FullPath}/**/*.xml"), parameters.Paths.Directories.TestCoverageOutputResults, settings);
    });

Task("Copy-Files")
    .Does<BuildParameters>((parameters) => 
    {
        Information("Copy static files to artifacts"); 
        CopyFileToDirectory("./LICENSE.md", parameters.Paths.Directories.Artifacts);
        CopyFiles(GetFiles("./src/resources/**/*"), parameters.Paths.Directories.ArtifactsStaticResources);

        foreach (var project in parameters.Paths.Files.PluginProjects)
        {
            var settings = new DotNetCorePublishSettings 
            {
                NoBuild = true,
                NoRestore = true,
                Configuration = parameters.Configuration,
                OutputDirectory = $"{parameters.Paths.Directories.ArtifactsBin}/{project.GetFilenameWithoutExtension()}",
                Framework = parameters.Framework,
                MSBuildSettings = parameters.MSBuildSettings
            };

            Information("Run publish for {0} ({1}) to {2}", project.GetFilenameWithoutExtension(), settings.Framework, settings.OutputDirectory); 
            DotNetCorePublish(project.FullPath, settings);
        }
    });

Task("Link-Assemblies")
    .WithCriteria<BuildParameters>((context, parameters) => parameters.IsRunningOnWindows, "Build not run on Windows.")
    .Does<BuildParameters>((parameters) => 
    {
        var settings = new ILMergeSettings
        {
            ArgumentCustomization = args => args.Append($"/keyfile:{MakeAbsolute(parameters.StrongSignKey).FullPath}")
                .Append($"/log:{MakeAbsolute(parameters.Paths.Directories.ArtifactsLinked.CombineWithFilePath("log.txt")).FullPath}")
                .Append("/ndebug")
                .Append("/copyattrs")
        };

        foreach (var project in parameters.Paths.Files.PluginProjects)
        {
            var linkerConfiguration = DeserializeJsonFromFile<JObject>(File($"{project.GetDirectory()}/ilmerge.deps.json"));
            
            var projectName = project.GetFilenameWithoutExtension();
            var projectDir = parameters.Paths.Directories.ArtifactsBin.Combine($"{projectName}");
            var outputFile = $"{parameters.Paths.Directories.ArtifactsLinked}/{projectName}.dll";
            var primaryAssembly = File($"{MakeAbsolute(projectDir).FullPath}/{projectName}.dll");
            var assemblies = new List<FilePath>();
            foreach(var dep in linkerConfiguration.GetValue("deps")){
                var path = $"{projectDir.FullPath}/{dep}.dll";
                if(!FileExists(path))
                    Error($"File {path} not found!");
                assemblies.Add(File(path));
            }

            Information("Run static linking for {0}", projectName);          
            ILMerge(outputFile, primaryAssembly, assemblies, settings);
        }
    });

Task("Pack-Zip")
    .IsDependentOn("Link-Assemblies")
    .Does<BuildParameters>((parameters) =>
    {
        var temp = parameters.Paths.Directories.Artifacts.Combine(parameters.Version.SemVersion);
        CreateDirectory(temp);
        var plugins = temp.Combine("plugins");
        CreateDirectory(plugins);
        var resources = temp.Combine("resources");
        CreateDirectory(resources);

        CopyFiles($"{parameters.Paths.Directories.ArtifactsLinked}/**/*.dll", plugins);
        CopyFiles($"{parameters.Paths.Directories.ArtifactsStaticResources}/**/*", resources);

        Zip(temp, parameters.Paths.Files.ZipArtifact);

        DeleteDirectory(temp, new DeleteDirectorySettings 
        {
            Recursive = true
        });
    });

Task("Release-Notes")
    .WithCriteria<BuildParameters>((context, parameters) => parameters.IsRunningOnWindows,  "Release notes are generated only on Windows agents.")
    .WithCriteria<BuildParameters>((context, parameters) => parameters.IsRunningOnAzurePipeline, "Release notes are generated only on release agents.")
    .WithCriteria<BuildParameters>((context, parameters) => parameters.IsStableRelease(),   "Release notes are generated only for stable releases.")
    .Does<BuildParameters>((parameters) => 
    {
        GetReleaseNotes(parameters.Paths.Files.ReleaseNotes);
    });

Task("Copy")
    .IsDependentOn("Test")
    .IsDependentOn("Coverage-Report")
    .IsDependentOn("Copy-Files")
    .Does(() =>
    {

    });

Task("Pack")
    .IsDependentOn("Copy")
    .IsDependentOn("Pack-Zip")
    .Does(() =>
    {

    });

Task("Default")
    .IsDependentOn("Pack")
    .IsDependentOn("Release-Notes")
    .Does(() =>
    {

    });

/*
 * Execution
 */
var target = Argument("target", "Default");
RunTarget(target);
