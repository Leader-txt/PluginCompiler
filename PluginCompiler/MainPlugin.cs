using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis;
using Org.BouncyCastle.Tsp;
using System.Reflection;
using Terraria;
using TerrariaApi.Server;
using System.Diagnostics;
using System.Reflection.Metadata;

namespace PluginCompiler
{
    [ApiVersion(2, 1)]
    public class MainPlugin : TerrariaPlugin
    {
        public const string Reference = "Reference";
        public const string Src = "SourceCodes";
        public override string Author => "Leader";
        public override string Description => "dynamic plugin compiler";
        public override string Name => "PluginCompiler";
        public override Version Version => new Version(1, 0, 0, 0);
        private Main game;
        public MainPlugin(Main game) : base(game)
        {
            this.game = game;
        }

        public override void Initialize()
        {
            Directory.CreateDirectory(Reference);
            Directory.CreateDirectory(Src);
            Directory.GetDirectories(Src).ToList().ForEach(x => DynamicCompile(x));
        }
        private static readonly IEnumerable<string> DefaultNamespaces = new[]
{
            "System",
            "System.IO",
            "System.Net",
            "System.Linq",
            "System.Text",
            "System.Text.RegularExpressions",
            "System.Collections.Generic"
};
        private string[] GetFilesFromDir(string dir)
        {
            var res = new List<string>();
            res.AddRange(Directory.GetFiles(dir));
            foreach(var _dir in Directory.GetDirectories(dir))
            {
                res.AddRange(GetFilesFromDir(_dir));
            }
            return res.ToArray();
        }
        public void DynamicCompile(string directory)
        {
            var dynamicCode =string.Concat(GetFilesFromDir(directory).Select(x => File.ReadAllText(x)));
            if (dynamicCode != null)
            {
                try
                {
                    var syntaxTree = SyntaxFactory.ParseSyntaxTree(dynamicCode);
                    var references = Directory.GetFiles(Reference).Where(x=>x.EndsWith(".dll")).Select(x => MetadataReference.CreateFromFile(x)).ToList();
                    references.Add(MetadataReference.CreateFromFile(typeof(Console).Assembly.Location));
                    //references.Add(MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location));

                    string assenmblyName = directory.Replace("\\","/").Split('/')[^1];
                    CSharpCompilation compilation = CSharpCompilation.Create(
                        assenmblyName,
                        syntaxTrees: new[] { syntaxTree },
                        references: references,
                        options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

                    using (var ms = new MemoryStream())
                    {
                        // write IL code into memory
                        EmitResult result = compilation.Emit(ms);

                        if (!result.Success)
                        {
                            // handle exceptions
                            IEnumerable<Diagnostic> failures = result.Diagnostics.Where(diagnostic =>
                                diagnostic.IsWarningAsError ||
                                diagnostic.Severity == DiagnosticSeverity.Error);

                            foreach (Diagnostic diagnostic in failures)
                            {
                                Console.Error.WriteLine("{0}: {1} at {2}", diagnostic.Id, diagnostic.GetMessage(),diagnostic.Location);
                            }
                        }
                        else
                        {
                            // load this 'virtual' DLL so that we can use
                            ms.Seek(0, SeekOrigin.Begin);
                            Assembly objAssembly = Assembly.Load(ms.ToArray());
                            var plugins = new List<PluginContainer>();
                            foreach (Type type in objAssembly.GetExportedTypes())
                            {
                                if (!type.IsSubclassOf(typeof(TerrariaPlugin)) || !type.IsPublic || type.IsAbstract)
                                    continue;
                                object[] customAttributes = type.GetCustomAttributes(typeof(ApiVersionAttribute), false);
                                if (customAttributes.Length == 0)
                                    continue;

                                TerrariaPlugin pluginInstance;
                                try
                                {
                                    pluginInstance = (TerrariaPlugin)Activator.CreateInstance(type, game);
                                }
                                catch (Exception ex)
                                {
                                    // Broken plugins better stop the entire server init.
                                    throw new InvalidOperationException(
                                        string.Format("Could not create an instance of plugin class \"{0}\".", type.FullName), ex);
                                }
                                plugins.Add(new PluginContainer(pluginInstance));
                            }

                            IOrderedEnumerable<PluginContainer> orderedPluginSelector =
                                from x in plugins
                                orderby x.Plugin.Order, x.Plugin.Name
                                select x;


                            foreach (PluginContainer current in orderedPluginSelector)
                            {

                                try
                                {
                                    current.Initialize();
                                }
                                catch (Exception ex)
                                {
                                    // Broken plugins better stop the entire server init.
                                    throw new InvalidOperationException(string.Format(
                                        "Plugin \"{0}\" has thrown an exception during initialization.", current.Plugin.Name), ex);
                                }
                                Console.WriteLine(string.Format(
                                    "Plugin {0} v{1} (by {2}) initiated.", current.Plugin.Name, current.Plugin.Version, current.Plugin.Author),
                                    TraceLevel.Info);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    throw ex;
                }
            }
            else
            {
                Console.WriteLine("DynamicCode is null!");
            }
        }

    }
}