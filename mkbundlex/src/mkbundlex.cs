#region Usings

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Mono.Unix;
using ICSharpCode.SharpZipLib.Zip.Compression.Streams;

#endregion

namespace mkbundlex
{
    internal class MakeBundle
    {
        private static string _output = "a.out";
        private static string _objectOut;
        private static readonly List<string> LinkPaths = new List<string>();
        private static bool _autodeps;
        private static bool _keeptemp;
        private static bool _compileOnly;
        private static bool _staticLink;
        private static string _configFile;
        private static string _machineConfigFile;
        private static string _configDir;
        private static string _style = "linux";
        private static bool _compress;
        private static bool _nomain;
        private static readonly string[] Chars = new string[256];

        private static bool IsUnix
        {
            get
            {
                var p = (int) Environment.OSVersion.Platform;
                return ((p == 4) || (p == 128) || (p == 6));
            }
        }

        private static int Main(string[] args)
        {
            var sources = new List<string>();
            var top = args.Length;
            LinkPaths.Add(".");

            DetectOS();

            for (var i = 0; i < top; i++)
            {
                switch (args[i])
                {
                    case "--help":
                    case "-h":
                    case "-?":
                        Help();
                        return 1;

                    case "-c":
                        _compileOnly = true;
                        break;

                    case "-o":
                        if (i + 1 == top)
                        {
                            Help();
                            return 1;
                        }
                        _output = args[++i];
                        break;

                    case "-oo":
                        if (i + 1 == top)
                        {
                            Help();
                            return 1;
                        }
                        _objectOut = args[++i];
                        break;

                    case "-L":
                        if (i + 1 == top)
                        {
                            Help();
                            return 1;
                        }
                        LinkPaths.Add(args[++i]);
                        break;

                    case "--nodeps":
                        _autodeps = false;
                        break;

                    case "--deps":
                        _autodeps = true;
                        break;

                    case "--keeptemp":
                        _keeptemp = true;
                        break;
                    case "--static":
                        if (_style == "windows")
                        {
                            Console.Error.WriteLine("The option `{0}' is not supported on this platform.", args[i]);
                            return 1;
                        }
                        _staticLink = true;
                        Console.WriteLine(
                            "Note that statically linking the LGPL Mono runtime has more licensing restrictions than dynamically linking.");
                        Console.WriteLine("See http://www.mono-project.com/Licensing for details on licensing.");
                        break;
                    case "--config":
                        if (i + 1 == top)
                        {
                            Help();
                            return 1;
                        }

                        _configFile = args[++i];
                        break;
                    case "--machine-config":
                        if (i + 1 == top)
                        {
                            Help();
                            return 1;
                        }

                        _machineConfigFile = args[++i];

                        Console.WriteLine(
                            "WARNING:\n  Check that the machine.config file you are bundling\n  doesn't contain sensitive information specific to this machine.");
                        break;
                    case "--config-dir":
                        if (i + 1 == top)
                        {
                            Help();
                            return 1;
                        }

                        _configDir = args[++i];
                        break;
                    case "-z":
                        _compress = true;
                        break;
                    case "--nomain":
                        _nomain = true;
                        break;
                    default:
                        sources.Add(args[i]);
                        break;
                }
            }

            Console.WriteLine("Sources: {0} Auto-dependencies: {1}", sources.Count, _autodeps);
            if (sources.Count == 0 || _output == null)
            {
                Help();
                Environment.Exit(1);
            }

            var assemblies = LoadAssemblies(sources);

            if (_autodeps)
            {
                var refs = assemblies.SelectMany(x => x.GetReferencedAssemblies()).Distinct().ToList();
                LoadReferences(refs, assemblies);
            }


            var files = assemblies.Select(asm => asm.CodeBase).ToList();

            // Special casing mscorlib.dll: any specified mscorlib.dll cannot be loaded
            // by Assembly.ReflectionFromLoadFrom(). Instead the fx assembly which runs
            // mkbundle.exe is loaded, which is not what we want.
            // So, replace it with whatever actually specified.
            foreach (var srcfile in sources.Where(srcfile => Path.GetFileName(srcfile) == "mscorlib.dll"))
            {
                foreach (var file in files.Where(file => Path.GetFileName(new Uri(file).LocalPath) == "mscorlib.dll"))
                {
                    files.Remove(file);
                    files.Add(new Uri(Path.GetFullPath(srcfile)).LocalPath);
                    break;
                }
                break;
            }

            GenerateBundles(files);
            //GenerateJitWrapper ();

            return 0;
        }

        private static void WriteSymbol(TextWriter sw, string name, long size)
        {
            switch (_style)
            {
                case "linux":
                    sw.WriteLine(
                        ".globl {0}\n" +
                        "\t.section .rodata\n" +
                        "\t.p2align 5\n" +
                        "\t.type {0}, \"object\"\n" +
                        "\t.size {0}, {1}\n" +
                        "{0}:\n",
                        name, size);
                    break;
                case "osx":
                    sw.WriteLine(
                        "\t.section __TEXT,__text,regular,pure_instructions\n" +
                        "\t.globl _{0}\n" +
                        "\t.data\n" +
                        "\t.align 4\n" +
                        "_{0}:\n",
                        name, size);
                    break;
                case "windows":
                    sw.WriteLine(
                        ".globl _{0}\n" +
                        "\t.section .rdata,\"dr\"\n" +
                        "\t.align 32\n" +
                        "_{0}:\n",
                        name, size);
                    break;
            }
        }

        private static void WriteBuffer(TextWriter ts, Stream stream, byte[] buffer)
        {
            int n;

            // Preallocate the strings we need.
            if (Chars[0] == null)
            {
                for (var i = 0; i < Chars.Length; i++)
                    Chars[i] = string.Format("\t.byte {0}\n", i);
            }

            while ((n = stream.Read(buffer, 0, buffer.Length)) != 0)
            {
                for (var i = 0; i < n; i++)
                    ts.Write(Chars[buffer[i]]);
            }

            ts.WriteLine();
        }

        private static void GenerateBundles(IEnumerable files)
        {
            var temp_s = "temp.s"; // Path.GetTempFileName ();
            var tempC = "temp.c";
            var tempO = "temp.o";

            if (_compileOnly)
                tempC = _output;
            if (_objectOut != null)
                tempO = _objectOut;

            try
            {
                var cBundleNames = new ArrayList();
                var configNames = new ArrayList();
                var buffer = new byte[8192];

                using (var ts = new StreamWriter(File.Create(temp_s)))
                {
                    using (var tc = new StreamWriter(File.Create(tempC)))
                    {
                        string prog = null;

                        tc.WriteLine("/* This source code was produced by mkbundle, do not edit */");
                        tc.WriteLine("#include <mono/metadata/mono-config.h>");
                        tc.WriteLine("#include <mono/metadata/assembly.h>\n");

                        if (_compress)
                        {
                            tc.WriteLine("typedef struct _compressed_data {");
                            tc.WriteLine("\tMonoBundledAssembly assembly;");
                            tc.WriteLine("\tint compressed_size;");
                            tc.WriteLine("} CompressedAssembly;\n");
                        }

                        foreach (string url in files)
                        {
                            var fname = new Uri(url).LocalPath;
                            var aname = Path.GetFileName(fname);
                            var encoded = aname.Replace("-", "_").Replace(".", "_");

                            if (prog == null)
                                prog = aname;

                            Console.WriteLine("   embedding: " + fname);

                            Stream stream = File.OpenRead(fname);

                            // Compression can be parallelized
                            var realSize = stream.Length;
                            if (_compress)
                            {
                                var ms = new MemoryStream();
                                var deflate = new DeflaterOutputStream(ms);
                                int n;
                                while ((n = stream.Read(buffer, 0, buffer.Length)) != 0)
                                {
                                    deflate.Write(buffer, 0, n);
                                }
                                stream.Close();
                                deflate.Finish();
                                var bytes = ms.GetBuffer();
                                stream = new MemoryStream(bytes, 0, (int) ms.Length, false, false);
                            }

                            WriteSymbol(ts, "assembly_data_" + encoded, stream.Length);

                            WriteBuffer(ts, stream, buffer);

                            if (_compress)
                            {
                                tc.WriteLine("extern const unsigned char assembly_data_{0} [];", encoded);
                                tc.WriteLine("static CompressedAssembly assembly_bundle_{0} = {{{{\"{1}\"," +
                                             " assembly_data_{0}, {2}}}, {3}}};",
                                             encoded, aname, realSize, stream.Length);
                                var ratio = ((double) stream.Length*100)/realSize;
                                Console.WriteLine("   compression ratio: {0:.00}%", ratio);
                            }
                            else
                            {
                                tc.WriteLine("extern const unsigned char assembly_data_{0} [];", encoded);
                                tc.WriteLine(
                                    "static const MonoBundledAssembly assembly_bundle_{0} = {{\"{1}\", assembly_data_{0}, {2}}};",
                                    encoded, aname, realSize);
                            }
                            stream.Close();

                            cBundleNames.Add("assembly_bundle_" + encoded);

                            try
                            {
                                var cf = File.OpenRead(fname + ".config");
                                Console.WriteLine(" config from: " + fname + ".config");
                                tc.WriteLine("extern const unsigned char assembly_config_{0} [];", encoded);
                                WriteSymbol(ts, "assembly_config_" + encoded, cf.Length);
                                WriteBuffer(ts, cf, buffer);
                                ts.WriteLine();
                                configNames.Add(new[] {aname, encoded});
                            }
                            catch (FileNotFoundException)
                            {
                                /* we ignore if the config file doesn't exist */
                            }
                        }
                        if (_configFile != null)
                        {
                            FileStream conf;
                            try
                            {
                                conf = File.OpenRead(_configFile);
                            }
                            catch
                            {
                                Error(String.Format("Failure to open {0}", _configFile));
                                return;
                            }
                            Console.WriteLine("System config from: " + _configFile);
                            tc.WriteLine("extern const char system_config;");
                            WriteSymbol(ts, "system_config", _configFile.Length);

                            WriteBuffer(ts, conf, buffer);
                            // null terminator
                            ts.Write("\t.byte 0\n");
                            ts.WriteLine();
                        }

                        if (_machineConfigFile != null)
                        {
                            FileStream conf;
                            try
                            {
                                conf = File.OpenRead(_machineConfigFile);
                            }
                            catch
                            {
                                Error(String.Format("Failure to open {0}", _machineConfigFile));
                                return;
                            }
                            Console.WriteLine("Machine config from: " + _machineConfigFile);
                            tc.WriteLine("extern const char machine_config;");
                            WriteSymbol(ts, "machine_config", _machineConfigFile.Length);

                            WriteBuffer(ts, conf, buffer);
                            ts.Write("\t.byte 0\n");
                            ts.WriteLine();
                        }
                        ts.Close();

                        Console.WriteLine("Compiling:");
                        var cmd = String.Format("{0} -o {1} {2} ", GetEnv("AS", "as"), tempO, temp_s);
                        var ret = Execute(cmd);
                        if (ret != 0)
                        {
                            Error("[Fail]");
                            return;
                        }

                        tc.WriteLine(_compress
                                         ? "\nstatic const CompressedAssembly *compressed [] = {"
                                         : "\nstatic const MonoBundledAssembly *bundled [] = {");

                        foreach (string c in cBundleNames)
                        {
                            tc.WriteLine("\t&{0},", c);
                        }
                        tc.WriteLine("\tNULL\n};\n");
                        tc.WriteLine("static char *image_name = \"{0}\";", prog);

                        tc.WriteLine("\nstatic void install_dll_config_files (void) {\n");
                        foreach (string[] ass in configNames)
                        {
                            tc.WriteLine("\tmono_register_config_for_assembly (\"{0}\", assembly_config_{1});\n", ass[0],
                                         ass[1]);
                        }
                        if (_configFile != null)
                            tc.WriteLine("\tmono_config_parse_memory (&system_config);\n");
                        if (_machineConfigFile != null)
                            tc.WriteLine("\tmono_register_machine_config (&machine_config);\n");
                        tc.WriteLine("}\n");

                        if (_configDir != null)
                            tc.WriteLine("static const char *config_dir = \"{0}\";", _configDir);
                        else
                            tc.WriteLine("static const char *config_dir = NULL;");

                        var templateStream = Assembly.GetAssembly(typeof (MakeBundle)).GetManifestResourceStream(_compress ? "template_z.c" : "template.c");

                        var s = new StreamReader(templateStream);
                        var template = s.ReadToEnd();
                        tc.Write(template);

                        if (!_nomain)
                        {
                            var templateMainStream = Assembly.GetAssembly(typeof (MakeBundle)).GetManifestResourceStream("template_main.c");
                            var st = new StreamReader(templateMainStream);
                            var maintemplate = st.ReadToEnd();
                            tc.Write(maintemplate);
                        }

                        tc.Close();

                        if (_compileOnly)
                            return;

                        var zlib = (_compress ? "-lz" : "");
                        var debugging = "-g";
                        var cc = GetEnv("CC", IsUnix ? "cc" : "gcc -mno-cygwin");

                        if (_style == "linux")
                            debugging = "-ggdb";
                        if (_staticLink)
                        {
                            var smonolib = _style == "osx" ? "`pkg-config --variable=libdir mono-2`/libmono-2.0.a " : "-Wl,-Bstatic -lmono-2.0 -Wl,-Bdynamic ";
                            cmd = String.Format("{4} -o {2} -Wall `pkg-config --cflags mono-2` {0} {3} " +
                                                "`pkg-config --libs-only-L mono-2` " + smonolib +
                                                "`pkg-config --libs-only-l mono-2 | sed -e \"s/\\-lmono-2.0 //\"` {1}",
                                                tempC, tempO, _output, zlib, cc);
                        }
                        else
                        {
                            cmd =
                                String.Format(
                                    "{4} " + debugging + " -o {2} -Wall {0} `pkg-config --cflags --libs mono-2` {3} {1}",
                                    tempC, tempO, _output, zlib, cc);
                        }

                        ret = Execute(cmd);
                        if (ret != 0)
                        {
                            Error("[Fail]");
                            return;
                        }
                        Console.WriteLine("Done");
                    }
                }
            }
            finally
            {
                if (!_keeptemp)
                {
                    if (_objectOut == null)
                    {
                        File.Delete(tempO);
                    }
                    if (!_compileOnly)
                    {
                        File.Delete(tempC);
                    }
                    File.Delete(temp_s);
                }
            }
        }

        private static List<Assembly> LoadAssemblies(IEnumerable<string> sources)
        {
            var assemblies = new List<Assembly>();
            var error = false;

            foreach (var a in sources.Select(LoadAssembly))
            {
                if (a == null)
                {
                    error = true;
                    continue;
                }

                assemblies.Add(a);
            }

            if (error)
                Environment.Exit(1);

            return assemblies;
        }

        private static void LoadReferences(IEnumerable<AssemblyName> references, List<Assembly> assemblies)
        {
            foreach (var asm in references)
            {
                if (assemblies.Select(x => x.GetName().Name).Contains(asm.Name))
                {
                    continue;
                }


                Console.WriteLine("Need: {0}", asm);
                var a = LoadReference(asm);

                if (a != null)
                {
                    assemblies.Add(a);

                    LoadReferences(a.GetReferencedAssemblies(), assemblies);
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Could not find reference: {0}", asm);
                    Console.ResetColor();

                    Environment.Exit(2);
                }
            }

        }

        private static Assembly LoadReference(AssemblyName assemblyName)
        {
            foreach (var path in LinkPaths.Select(x => Path.Combine(x, string.Format("{0}.dll", assemblyName.Name))))
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                try
                {
                    var a = Assembly.LoadFrom(path);

                    if (a.GetName().Name == assemblyName.Name)
                    {
                        return a;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Error load assemby {0} from {1} : {2}", assemblyName.FullName, path, ex);
                }
            }


            try
            {
                var a = Assembly.Load(assemblyName);

                return a;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error loading assemby {0} from GAC: {1}", assemblyName, ex);
            }

            return null;
        }

        private static Assembly LoadAssembly(string assembly)
        {
            Assembly a;

            try
            {
                char[] pathChars = {'/', '\\'};

                if (assembly.IndexOfAny(pathChars) != -1)
                {
                    a = Assembly.LoadFrom(assembly);
                }
                else
                {
                    var ass = assembly;
                    if (ass.EndsWith(".dll"))
                        ass = assembly.Substring(0, assembly.Length - 4);
                    a = Assembly.Load(ass);
                }
                return a;
            }
            catch (FileNotFoundException)
            {
                var totalLog = "";

                foreach (string dir in LinkPaths)
                {
                    var fullPath = Path.Combine(dir, assembly);
                    if (!assembly.EndsWith(".dll") && !assembly.EndsWith(".exe"))
                        fullPath += ".dll";

                    try
                    {
                        a = Assembly.LoadFrom(fullPath);
                        return a;
                    }
                    catch (FileNotFoundException ff)
                    {
                        totalLog += ff.FusionLog;
                    }
                }
                Error("Cannot find assembly `" + assembly + "'");
                Console.WriteLine("Log: \n" + totalLog);
            }
            catch (BadImageFormatException f)
            {
                Error("Cannot load assembly (bad file format)" + f.FusionLog);
            }
            catch (FileLoadException f)
            {
                Error("Cannot load assembly " + f.FusionLog);
            }
            catch (ArgumentNullException)
            {
                Error("Cannot load assembly (null argument)");
            }
            return null;
        }

        private static void Error(string msg)
        {
            Console.Error.WriteLine(msg);
            Environment.Exit(1);
        }

        private static void Help()
        {
            Console.WriteLine("Usage is: mkbundle [options] assembly1 [assembly2...]\n\n" +
                              "Options:\n" +
                              "    -c                  Produce stub only, do not compile\n" +
                              "    -o out              Specifies output filename\n" +
                              "    -oo obj             Specifies output filename for helper object file\n" +
                              "    -L path             Adds `path' to the search path for assemblies\n" +
                              "    --nodeps            Turns off automatic dependency embedding (default)\n" +
                              "    --deps              Turns on automatic dependency embedding\n" +
                              "    --keeptemp          Keeps the temporary files\n" +
                              "    --config F          Bundle system config file `F'\n" +
                              "    --config-dir D      Set MONO_CFG_DIR to `D'\n" +
                              "    --machine-config F  Use the given file as the machine.config for the application.\n" +
                              "    --static            Statically link to mono libs\n" +
                              "    --nomain            Don't include a main() function, for libraries\n" +
                              "    -z                  Compress the assemblies before embedding.\n" +
                              "                        You need zlib development headers and libraries.\n");
        }

        [DllImport("libc")]
        private static extern int system(string s);

        [DllImport("libc")]
        private static extern int uname(IntPtr buf);

        private static void DetectOS()
        {
            if (!IsUnix)
            {
                Console.WriteLine("OS is: Windows");
                _style = "windows";
                return;
            }

            IntPtr buf = UnixMarshal.AllocHeap(8192);
            if (uname(buf) != 0)
            {
                Console.WriteLine("Warning: Unable to detect OS");
                UnixMarshal.FreeHeap(buf);
                return;
            }
            var os = Marshal.PtrToStringAnsi(buf);
            Console.WriteLine("OS is: " + os);
            if (os == "Darwin")
                _style = "osx";

            UnixMarshal.FreeHeap(buf);
        }

        private static int Execute(string cmdLine)
        {
            if (IsUnix)
            {
                Console.WriteLine(cmdLine);
                return system(cmdLine);
            }

            // on Windows, we have to pipe the output of a
            // `cmd` interpolation to dos2unix, because the shell does not
            // strip the CRLFs generated by the native pkg-config distributed
            // with Mono.
            var b = new StringBuilder();
            var count = 0;
            foreach (var t in cmdLine)
            {
                if (t == '`')
                {
                    if (count%2 != 0)
                    {
                        b.Append("|dos2unix");
                    }
                    count++;
                }
                b.Append(t);
            }
            cmdLine = b.ToString();
            Console.WriteLine(cmdLine);

            var psi = new ProcessStartInfo
                {
                    UseShellExecute = false,
                    FileName = "sh",
                    Arguments = String.Format("-c \"{0}\"", cmdLine)
                };

            using (var p = Process.Start(psi))
            {
                p.WaitForExit();
                return p.ExitCode;
            }
        }

        private static string GetEnv(string name, string defaultValue)
        {
            var s = Environment.GetEnvironmentVariable(name);
            return s ?? defaultValue;
        }
    }
}