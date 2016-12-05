using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Newtonsoft.Json;

namespace UnderscoreLambdasGithub
{
    public class Program
    {
        //static readonly Dictionary<string, int> underscoreRepos = new Dictionary<string, int>();

        public static void Main()
        {
            //Repos = GetRepos().ToArray();

            var commonOptions = new ExecutionDataflowBlockOptions { BoundedCapacity = 1 };
            var parallelOptions = new ExecutionDataflowBlockOptions
            {
                BoundedCapacity = 2,
                MaxDegreeOfParallelism = 2,
                EnsureOrdered = false
            };

            var cloneBlock = new TransformBlock<string, string>(repo => Clone(repo), parallelOptions);

            var getFilesBlock = new TransformManyBlock<string, string>(path => GetFiles(path), commonOptions);

            getFilesBlock.SendAsync(BasePath).Wait();

            var parseBlock = new TransformBlock<string, LambdaData>(file => Parse(file), commonOptions);

            LambdaData lambdaDataSummary = new LambdaData();
            var summarizeBlock = new ActionBlock<LambdaData>(ld => lambdaDataSummary.Add(ld));

            var linkOptions = new DataflowLinkOptions { PropagateCompletion = true };

            cloneBlock.LinkTo(getFilesBlock, linkOptions);
            getFilesBlock.LinkTo(parseBlock, linkOptions);
            parseBlock.LinkTo(summarizeBlock, linkOptions);

            var sendDataTask = SendData(cloneBlock);

            summarizeBlock.Completion.Wait();

            sendDataTask.Wait();

            Print(lambdaDataSummary);

            Console.WriteLine();

            //Print(underscoreRepos);
        }

        private static void Print(LambdaData lambdaData)
        {
            Console.WriteLine($"single lambdas: {lambdaData.TotalSingleLambdasCount}, multi lambdas: {lambdaData.TotalMultiLambdasCount}");

            Console.WriteLine("Single lambdas:");
            Print(lambdaData.SingleLambdas);

            Console.WriteLine("Multi lambdas, one parameter:");
            Print(lambdaData.MultiLambdasOneParameter);

            Console.WriteLine("Multi lambdas, multi parameters:");
            Print(lambdaData.MultiLambdasMultiParameters);
        }

        private static void Print(Dictionary<string, int> dictionary)
        {
            Console.WriteLine($"Total: {dictionary.Values.Sum()}");

            var data = dictionary.OrderByDescending(kvp => kvp.Value).Take(10);

            foreach (var kvp in data)
            {
                Console.WriteLine($"{kvp.Key}\t{kvp.Value}");
            }
        }

        private static readonly string BasePath = @"C:\Users\Svick\AppData\Local\Temp\UnderscoreLambdasGithub\";

        private static async Task SendData(ITargetBlock<string> targetBlock)
        {
            foreach (var repo in Repos ?? new string[0])
            {
                await targetBlock.SendAsync(repo);
            }

            targetBlock.Complete();
        }

        private static LambdaData Parse(string file)
        {
            Console.WriteLine($"Parsing {file.Substring(BasePath.Length)}.");

            var lambdaData = new LambdaData();

            var syntaxTree = CSharpSyntaxTree.ParseText(SourceText.From(File.OpenRead(file)));
            var semanticModel = CSharpCompilation.Create(null).AddSyntaxTrees(syntaxTree).GetSemanticModel(syntaxTree);

            var lambdas = syntaxTree.GetCompilationUnitRoot().DescendantNodes().OfType<LambdaExpressionSyntax>();

            foreach (var lambda in lambdas)
            {
                var names = lambda.DescendantNodes().OfType<NameSyntax>().Select(name => semanticModel.GetSymbolInfo(name).Symbol).Where(s => s != null).ToList();

                var simpleLambda = lambda as SimpleLambdaExpressionSyntax;
                if (simpleLambda != null)
                {
                    var paramaterSymbol = semanticModel.GetDeclaredSymbol(simpleLambda.Parameter);

                    if (SingleLambda(lambdaData, names, paramaterSymbol))
                    {
                        //LambdaData.Increment(underscoreRepos, file.Substring(BasePath.Length).Split('\\')[0]);
                    }
                }

                var complexLambda = lambda as ParenthesizedLambdaExpressionSyntax;
                if (complexLambda != null)
                {
                    var parameterSymbols = complexLambda.ParameterList.Parameters
                        .Select(param => semanticModel.GetDeclaredSymbol(param))
                        .ToList();

                    if (parameterSymbols.Count == 1)
                    {
                        if (SingleLambda(lambdaData, names, parameterSymbols.Single()))
                        {
                            //LambdaData.Increment(underscoreRepos, file.Substring(BasePath.Length).Split('\\')[0]);
                        }
                    }
                    else
                    {
                        MultiLambda(lambdaData, names, parameterSymbols);
                    }
                }
            }

            return lambdaData;
        }

        private static void MultiLambda(LambdaData lambdaData, List<ISymbol> names, List<IParameterSymbol> parameterSymbols)
        {
            lambdaData.MultiLambda();

            var missingSymbols =
                (from parameterSymbol in parameterSymbols
                 where !names.Contains(parameterSymbol)
                 select parameterSymbol.Name).ToList();

            switch (missingSymbols.Count)
            {
                case 0:
                    break;
                case 1:
                    lambdaData.MultiLambdaOneUnused(missingSymbols.Single());
                    break;
                default:
                    lambdaData.MultiLambdaMultiUnused(string.Join(", ", missingSymbols));
                    break;
            }
        }

        private static bool SingleLambda(LambdaData lambdaData, List<ISymbol> names, IParameterSymbol parameterSymbol)
        {
            lambdaData.SingleLambda();

            if (!names.Contains(parameterSymbol))
            {
                lambdaData.SingleLambdaUnused(parameterSymbol.Name);

                if (parameterSymbol.Name == "_")
                {
                    return true;
                }
            }

            return false;
        }

        private static IEnumerable<string> GetFiles(string path)
            => Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories);

        private static string Clone(string repo)
        {
            Console.WriteLine(repo);

            string gitUrl = $"https://github.com/{repo}.git";
            string path = Path.Combine(BasePath, repo.Split('/')[1]);

            if (Directory.Exists(path))
                Console.WriteLine($"Directory for {repo} already exists.");
            else
                Process.Start("git", $"clone {gitUrl} {path} --depth 1").WaitForExit();

            return path;
        }

        //// https://github.com/trending/csharp on 2016-11-07
        //private static readonly string[] Repos =
        //{
        //    "Mrs4s/BaiduPanDownload",
        //    "shadowsocks/shadowsocks-windows",
        //    "Microsoft/aspnet-api-versioning",
        //    "lolp1/Overlay.NET",
        //    "mxgmn/WaveFunctionCollapse",
        //    "dotnet-state-machine/stateless",
        //    "telerik/JustDecompileEngine",
        //    "anakic/Jot",
        //    "JimBobSquarePants/ImageSharp",
        //    "Redth/PushSharp",
        //    "aspnet/Mvc",
        //    "aspnet/EntityFramework",
        //    "HangfireIO/Hangfire",
        //    "google/sandbox-attacksurface-analysis-tools",
        //    "serilog/serilog",
        //    "dotnet/roslyn",
        //    "thestonefox/VRTK",
        //    "Microsoft/UWPCommunityToolkit",
        //    "Unity-Technologies/PostProcessing",
        //    "PowerShell/PowerShell",
        //    "StackExchange/dapper-dot-net",
        //    "Reactive-Extensions/Rx.NET",
        //    "neuecc/UniRx",
        //    "JFrogDev/project-examples",
        //    "aspnet/Razor"
        //};

        private static string[] Repos;

        static IEnumerable<string> GetRepos()
        {
            var client = new HttpClient();

            client.DefaultRequestHeaders.UserAgent.ParseAdd("gsvick at gmail.com");

            var jsonString = client.GetStringAsync(
                "https://api.github.com/search/repositories?q=language:csharp+stars:%3C=100&sort=updated&per_page=100").Result;

            dynamic data = JsonConvert.DeserializeObject(jsonString);
            foreach (var repo in data.items)
            {
                yield return repo.full_name;
            }
        }

//https://github.com/nathandiill/16T3-MDU112.2.git
//https://github.com/asterales/2DCapstoneGame.git
//https://github.com/K0alele/3dTankGame.git
//https://github.com/krethh/AISDE_1.git
//https://github.com/limvi-licef/AR-driving-assistant.git
//https://github.com/QAProject7/Accelify.git
//https://github.com/avera813/AddressBookWPF.git
//https://github.com/tainicom/Aether.Extras.git
//https://github.com/AnalogIO/Analog-ShiftPlanner.git
//https://github.com/superlapp/AppMonitor.git
//https://github.com/Apptracktive/AptkAma.git
//https://github.com/omrkrgz/Ar-zaServis.git
//https://github.com/seanpatrickw9/Assign4.git
//https://github.com/Lombiq/Associativy-Administration.git
//https://github.com/Lombiq/Associativy-Core.git
//https://github.com/Lombiq/Associativy-Extensions.git
//https://github.com/Lombiq/Associativy-Frontend-Engines-Administration.git
//https://github.com/Lombiq/Associativy-Neo4j-Driver.git
//https://github.com/Lombiq/Associativy-Tag-Nodes.git
//https://github.com/Lombiq/Associativy-Taxonomies-Adapter.git
//https://github.com/Lombiq/Associativy-Web-Services.git
//https://github.com/Lombiq/Associativy-Wikipedia-Instance.git
//https://github.com/TerryDieckmann/Aura.git
//https://github.com/Johnson-Law-Group/Auto-Report-Scheduler.git
//https://github.com/vitalbit/AutomataDiplom.git
//https://github.com/davidschep/BabyGamesDah.git
//https://github.com/Brazenology/Batch-Record-Generator.git
//https://github.com/FWidm/BeerRouting.git
//https://github.com/cagina/Birthday-Paradox.git
//https://github.com/korypostma/BlackSpace.git
//https://github.com/blueicek4/Blt---Patenti-One-Touch.git
//https://github.com/OneClickSoftware/BrickOuch.git
//https://github.com/ProductiveRage/Bridge.React.git
//https://github.com/jr091291/BussinesViews.git
//https://github.com/jtrain184/C-Sharp-Pluralsight-Coursework.git
//https://github.com/Burgund/C-Sharp_WPF_Clicker_Game_Engine.git
//https://github.com/Graywords/CORE.git
//https://github.com/jgarmos/Calculator.git
//https://github.com/elawat/Chapter04.git
//https://github.com/Msatkin/ChatRoom.git
//https://github.com/RabdPnguin/CliNet.git
//https://github.com/MicrosoftLearning/CloudEnabledApps.git
//https://github.com/Atenna/CodilitySolutions.git
//https://github.com/LiamCaliceGameDev/ColorShooter.git
//https://github.com/Lombiq/Combinator.git
//https://github.com/Lombiq/Combinator-CoffeeScript-Preprocessor.git
//https://github.com/Kojak420/Comp1004-Assignment3.git
//https://github.com/MissGeekBunny/ConventionMagicDemo.git
//https://github.com/Bomfim/DBMS.git
//https://github.com/PerunPlayer/DataBases.git
//https://github.com/tadams0/DebateSchedulerPrototype.git
//https://github.com/T-800/DefencesImmunitaires.git
//https://github.com/clmcgrath/DiagnosticsSharp.git
//https://github.com/jacknutkins/DynamicsCRM_API.git
//https://github.com/jonathandao0/ECE-480-Project.git
//https://github.com/wladi0/Elobuddy.git
//https://github.com/MicrosoftLearning/EntityFramework.git
//https://github.com/danielignatov/EntityFramework-CodeFirst-BookShopSystem.git
//https://github.com/pavel1yakimovich/EqualityOfDTO.git
//https://github.com/Stelmashenko-A/EventEmitter.git
//https://github.com/kay9911/FG5EParser.git
//https://github.com/SeniorProjectUSF-2016/Face-Recognition.git
//https://github.com/amaidie90/FinalProject.git
//https://github.com/rluck0419/FirstPersonDungeon.git
//https://github.com/ciiwolstudio/FleeOrFace.git
//https://github.com/KimGrip/FlourPower.git
//https://github.com/kojotek/Fuzzy-Logic-Project.git
//https://github.com/JarnoVosDeltion/GMD1B-JarnoVos.git
//https://github.com/JoppeStijfDeltion/GMD1B-JoppeStijf.git
//https://github.com/bartekkois/GPONMonitor.git
//https://github.com/victormartinezsimon/GamePlayRoll.git
//https://github.com/MaxDelavalle/GameTerminal.git
//https://github.com/Lombiq/Git-hg-Mirror-Daemon.git
//https://github.com/ThomasVlekkeDeltion/Gmd1B-ThomasVlekke.git
//https://github.com/JordanBoulan/HUDForPAVUnity.git
//https://github.com/Lombiq/Hastlayer-Demo.git
//https://github.com/solotraze/HelloWebAPI.git
//https://github.com/hqtogit/HelpfulLibraries.git
//https://github.com/Alex-Dobrynin/IMC.git
//https://github.com/tadeuferreira/IS.git
//https://github.com/JonasGao/ImageMerge.git
//https://github.com/Jcole429/Infinite-Runner.git
//https://github.com/tiyhouston/Instaclone-backend.git
//https://github.com/MitkoZ/IsTheNumberInTheArrayCheck.git
//https://github.com/romambler/Isaev_Roma_Task0.git
//https://github.com/clbjm/JsObjects.git
//https://github.com/NoProblem2000/KursachDB.git
//https://github.com/AnthonyArmatas/Lab3_Calculator.git
//https://github.com/LanguagePatches/LanguagePatches-Framework.git
//https://github.com/rachelbarnes/LinkedLists.git
//https://github.com/fuok/LiveDemo.git
//https://github.com/rakka74/LocalNicoMyList.git
//https://github.com/Lombiq/Lombiq-Projections.git
//https://github.com/innovhtk/MDW-wf.git
//https://github.com/moXnesdesign/MMVR16.git
//https://github.com/RSzkorla/Matrix.git
//https://github.com/2RealStudios/MazeGame.git
//https://github.com/Asnivor/MedLaunch.git
//https://github.com/spsei-programming/MembersCode.git
//https://github.com/Mowjoh/Meteor-Skin-Library.git
//https://github.com/hiroki-kitahara/MineS.git
//https://github.com/gresash/Minibeg_v2.git
//https://github.com/Sqeed/ModBotMkII.git
//https://github.com/cbayerlein/MouseHelper.git
//https://github.com/lovelll/MyAEProgram.git
//https://github.com/anuraj/MyCoreBlog.git
//https://github.com/deanilvincent/MyRestClientwithMVVM.git
//https://github.com/AndriiTur/MyTestListProject.git
//https://github.com/jpdominguez/NAVIS-Tool.git
//https://github.com/Malgosiek/NJPO2.git
//https://github.com/Silph-Road/Necrobot2.git
//https://github.com/delold/Notiwin.git
//https://github.com/Lombiq/Orchard-Abstractions.git
//https://github.com/Lombiq/Orchard-Abstractions-Examples.git
//https://github.com/Lombiq/Orchard-Antispam.git
//https://github.com/Lombiq/Orchard-Azure-Application-Insights.git
//https://github.com/Lombiq/Orchard-Azure-Indexing.git
//https://github.com/Lombiq/Orchard-BBCode.git
//https://github.com/Lombiq/Orchard-Background-Task-Viewer.git
//https://github.com/Lombiq/Orchard-Content-Types.git
//https://github.com/Lombiq/Orchard-Content-Widgets.git
//https://github.com/Lombiq/Orchard-Distributed-Events.git
//https://github.com/Lombiq/Orchard-Download-As.git
//https://github.com/Lombiq/Orchard-External-Pages.git
//https://github.com/Lombiq/Orchard-Facebook-Suite.git
//https://github.com/Lombiq/Orchard-Feed-Aggregator.git
//https://github.com/Lombiq/Orchard-JavaScript.Net.git
//https://github.com/Lombiq/Orchard-Liquid-Markup.git
//https://github.com/Lombiq/Orchard-Login-as-Anybody.git
//https://github.com/Lombiq/Orchard-Read-only.git
//https://github.com/Lombiq/Orchard-Recipe-Remote-Executor.git
//https://github.com/Lombiq/Orchard-Repository-Markdown-Content.git
//https://github.com/Lombiq/Orchard-RestSharp.git
//https://github.com/Lombiq/Orchard-Route-Permissions.git
//https://github.com/Lombiq/Orchard-Scripting-Extensions.git
//https://github.com/Lombiq/Orchard-Scripting-Extensions-DotNet.git
//https://github.com/Lombiq/Orchard-Scripting-Extensions-PHP.git
//https://github.com/Lombiq/Orchard-Target-Blank.git
//https://github.com/Lombiq/Orchard-Theme-Override.git
//https://github.com/Lombiq/Orchard-Training-Demo-Module.git
//https://github.com/Lombiq/Orchard-User-Notifications.git
//https://github.com/urbanit/OrchardCMS.Poll.git
//https://github.com/hqtogit/OrchardLiquid.git
//https://github.com/sirdoombox/Overwatch.Net.git
//https://github.com/dimitrijevic/PBService.git
//https://github.com/Silph-Road/POGOProtos.git
//https://github.com/SergiuSolomon/PatternsPizzaProject.git
//https://github.com/alexesca/PayrollSystem.git
//https://github.com/0xFireball/PenguinFiles.git
//https://github.com/SoftwareEngineeringProjectCharlesRobert/PerfectingMathSkills.git
//https://github.com/clpro1/Platinium.git
//https://github.com/phansford/PlayGround.git
//https://github.com/Xpressik/Podstawy-Gier-Komputerowych.git
//https://github.com/svenvermeulen/ProjectEuler.git
//https://github.com/Shavey/ProjectHarambe.git
//https://github.com/alex-doe/ProjectTime.git
//https://github.com/xSash/ProjetBlog.git
//https://github.com/AlexandreBarbosa/ProjetoSupriMedWeb-v1.0-.git
//https://github.com/EmilyWatsonCF/Promo.git
//https://github.com/AzureCAT-GSI/ProximityIotHackathon.git
//https://github.com/AlejandroBautista/ProyectoTerminal.git
//https://github.com/malahx/QuickMods.git
//https://github.com/oliwil/RecognitionClient.git
//https://github.com/SebastianKazmierczak/RemoteOSMC.git
//https://github.com/omarenriquez/RestaurantOrderProject-MIS15_FALL2016-.git
//https://github.com/thatsgerman/RomeCitySim.git
//https://github.com/LUDUSLab/Round2.git
//https://github.com/txsll/SLLInvoices.git
//https://github.com/ssui/SSI.Auditing.git
//https://github.com/truedreamsolutions/Sageer_Shahzad_C_Sharp_Files.git
//https://github.com/jrolstad/Sandbox.git
//https://github.com/Lukas0610/SharpScript.git
//https://github.com/ShepherdsLittleHelper/ShepherdsLittleHelper.git
//https://github.com/multidila/ShkolaSoftheme.git
//https://github.com/Radioh/SimpleBulletinBoard.git
//https://github.com/bryan2894-playgrnd/SimpleWeather-Windows.git
//https://github.com/pjomara/SoftwareEngineeringIIProject.git
//https://github.com/jiriKuba/SoundCollector.git
//https://github.com/YuriShporhun/SportsStore.git
//https://github.com/sanbir/StadiumBlockchainSimulator.git
//https://github.com/Inmigondra/Synesthebat.git
//https://github.com/bartoszkp/TDDEvaluation.git
//https://github.com/jphacks/TH_1602.git
//https://github.com/sethifur/Teamtosterone.git
//https://github.com/belchevgb/Telerik-Academy.git
//https://github.com/TeoXverT/Tesi-Triennale.git
//https://github.com/chixcancode/TextSentimentBot.git
//https://github.com/Jack3663/The-Mimic.git
//https://github.com/dev06/TheRightColor.git
//https://github.com/Nico4x4/TicTacToe.git
//https://github.com/Lombiq/Tidy-Orchard-Development-Toolkit.git
//https://github.com/ruifaguiar/TimeSheet.git
//https://github.com/MarshallPelissier/To-Do.git
//https://github.com/tiyhouston/Twitclone-backend.git
//https://github.com/Bswjtbanik/University_managment.git
//https://github.com/Parzival42/VACC.git
//https://github.com/fluendo/VAS.git
//https://github.com/LeagueOfDevelopers/VVKMusic.git
//https://github.com/hqtogit/Vandelay.git
//https://github.com/SenpaiCooki3/VisualStudio.git
//https://github.com/vjacquet/WmcSoft.git
//https://github.com/lovelll/YNDQHP.git
//https://github.com/geekloper/Youtube_Mp3_WinForm.git
//https://github.com/sergix1/addons.git
//https://github.com/cornell/apropos.git
//https://github.com/inau/autonomous.git
//https://github.com/troydahnertinterdyn/bmi.imis.Careers.git
//https://github.com/DouglasCristhian/br.com.weblayer.logistica.git
//https://github.com/Venefic/caveman.git
//https://github.com/fluendo/cerbero.git
//https://github.com/berman-lab/concat-windows.git
//https://github.com/Dolvondo/csharpclass1.git
//https://github.com/SkillsFundingAgency/das-employerapprenticeshipsservice.git
//https://github.com/primerb/ddf3g84s38fh48638.git
//https://github.com/eladlavi/dotnet_2016_June.git
//https://github.com/BPMed/finalproj.git
//https://github.com/rodolfocugler/fly.git
//https://github.com/jsigar/framing-the-sky.git
//https://github.com/ProvoDev/health-catalyst-app.git
//https://github.com/ZolotarenkoM/list_of_product.git
//https://github.com/szekemri/master.git
//https://github.com/Azure/migAz.git
//https://github.com/mojio/mojio.platform.sdk.git
//https://github.com/boveloco/olimpcs.git
//https://github.com/osprey-lang/osprey.git
//https://github.com/tema5190/quickup-hometasks.git
//https://github.com/shawnkoon/realtime-file-watcher.git
//https://github.com/agricola/shmup.git
//https://github.com/artempanko/simulation-office.git
//https://github.com/ProstoA/spower.git
//https://github.com/itvata90/test.git
//https://github.com/knownasilya/test-dotnet-core-mvc-api.git
//https://github.com/carola96/trabalho-jogo-da-forca.git
//https://github.com/thetrung/urb.git
    }

    class LambdaData
    {
        public int TotalSingleLambdasCount { get; private set; }
        public int TotalMultiLambdasCount { get; private set; }

        public Dictionary<string, int> SingleLambdas { get; } = new Dictionary<string, int>();
        public Dictionary<string, int> MultiLambdasOneParameter { get; } = new Dictionary<string, int>();
        public Dictionary<string, int> MultiLambdasMultiParameters { get; } = new Dictionary<string, int>();

        public void SingleLambda() => TotalSingleLambdasCount++;
        public void MultiLambda() => TotalMultiLambdasCount++;

        public static void Increment<T>(Dictionary<T, int> dictionary, T key, int added = 1)
        {
            int value;
            dictionary.TryGetValue(key, out value);
            dictionary[key] = value + added;
        }

        public void SingleLambdaUnused(string name) => Increment(SingleLambdas, name);
        public void MultiLambdaOneUnused(string name) => Increment(MultiLambdasOneParameter, name);
        public void MultiLambdaMultiUnused(string name) => Increment(MultiLambdasMultiParameters, name);

        private static void Add(Dictionary<string, int> thisDictionary, Dictionary<string, int> otherDictionary)
        {
            foreach (var kvp in otherDictionary)
            {
                Increment(thisDictionary, kvp.Key, kvp.Value);
            }
        }

        public void Add(LambdaData other)
        {
            this.TotalSingleLambdasCount += other.TotalSingleLambdasCount;
            this.TotalMultiLambdasCount += other.TotalMultiLambdasCount;

            Add(this.SingleLambdas, other.SingleLambdas);
            Add(this.MultiLambdasOneParameter, other.MultiLambdasOneParameter);
            Add(this.MultiLambdasMultiParameters, other.MultiLambdasMultiParameters);
        }
    }
}
