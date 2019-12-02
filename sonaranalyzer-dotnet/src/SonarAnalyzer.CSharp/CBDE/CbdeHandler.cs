﻿/*
 * SonarAnalyzer for .NET
 * Copyright (C) 2015-2019 SonarSource SA
 * mailto: contact AT sonarsource DOT com
 *
 * This program is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation; either
 * version 3 of the License, or (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with this program; if not, write to the Free Software Foundation,
 * Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.
 */

using System.Linq;
using System;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SonarAnalyzer.Helpers;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Runtime.InteropServices;

namespace SonarAnalyzer.Rules.CSharp
{
    public class CbdeHandler
    {
        Action<String, String, Location, CompilationAnalysisContext> raiseIssue;

        private const string cbdeJsonOutputFileName = "cbdeSEout.json";

        private static string cbdeBinaryPath;
        private string mlirDirectoryRoot;
        private string mlirDirectoryAssembly;
        private string cbdeJsonOutputPath;
        private string logFilePath;
        protected HashSet<string> csSourceFileNames= new HashSet<string>();
        protected Dictionary<string, int> fileNameDuplicateNumbering = new Dictionary<string, int>();
        private MemoryStream logStream;
        private StreamWriter logFile;
        private static readonly object logFileLock = new Object();
        private static readonly object metricsFileLock = new Object();
        private static readonly object perfFileLock = new Object();

        private static readonly string mlirPath =
            Environment.GetEnvironmentVariable("CIRRUS_WORKING_DIR") ?? // For Cirrus
            Environment.GetEnvironmentVariable("WORKSPACE") ?? // For Jenkins
            Path.GetTempPath(); // By default
        private static readonly string mlirProcessSpecificPath =
            Path.Combine(mlirPath, $"CBDE_{Process.GetCurrentProcess().Id}");
        private static readonly string mlirLogFile =
            Path.Combine(mlirProcessSpecificPath, "cbdeHandler.log");
        private static readonly string mlirMetricsLogFile =
            Path.Combine(mlirProcessSpecificPath, "metrics.log");
        private static readonly string mlirPerfLogFile =
            Path.Combine(mlirProcessSpecificPath, "performances.log");
        private static void GlobalLog(string s)
        {
            lock (logFileLock)
            {
                var message = $"{DateTime.Now} ({Thread.CurrentThread.ManagedThreadId,5}): {s}\n";
                File.AppendAllText(mlirLogFile, message);
            }
        }

        private void PerformanceLog(string s)
        {
            lock (perfFileLock)
            {
                File.AppendAllText(mlirPerfLogFile, s);
            }
        }

        static CbdeHandler()
        {
            Directory.CreateDirectory(mlirProcessSpecificPath);
            lock (logFileLock)
            {
                if (File.Exists(mlirProcessSpecificPath))
                {
                    File.Delete(mlirProcessSpecificPath);
                }
            }
            GlobalLog("Before unpack");
            UnpackCbdeExe();
            GlobalLog("After unpack");
        }
        public void Initialize(SonarAnalysisContext context, Action<String, String, Location, CompilationAnalysisContext> raiseIssue)
        {
            this.raiseIssue = raiseIssue;
            GlobalLog("Before initialize");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            if (cbdeBinaryPath != null)
            {
                RegisterMlirAndCbdeInOneStep(context);
            }
            watch.Stop();
            GlobalLog($"After initialize ({watch.ElapsedMilliseconds} ms)");
        }
        private void RegisterMlirAndCbdeInOneStep(SonarAnalysisContext context)
        {
            context.RegisterCompilationAction(
                c =>
                {
                    var compilationHash = c.Compilation.GetHashCode();
                    InitializePathsAndLog(c.Compilation.Assembly.Name, compilationHash);
                    GlobalLog("CBDE: Compilation phase");
                    var exporterMetrics = new MlirExporterMetrics();
                    try
                    {
                        foreach (var tree in c.Compilation.SyntaxTrees)
                        {
                            csSourceFileNames.Add(tree.FilePath);
                            GlobalLog($"CBDE: Treating file {tree.FilePath} in context {compilationHash}");
                            var mlirFileName = ManglePath(tree.FilePath) + ".mlir";
                            ExportFunctionMlir(tree, c.Compilation.GetSemanticModel(tree), exporterMetrics, mlirFileName);
                            logFile.WriteLine("- generated mlir file {0}", mlirFileName);
                            logFile.Flush();
                            GlobalLog($"CBDE: Done with file {tree.FilePath} in context {compilationHash}");
                        }
                        RunCbdeAndRaiseIssues(c);
                        GlobalLog("CBDE: End of compilation");
                        lock (metricsFileLock)
                        {
                            File.AppendAllText(mlirMetricsLogFile, exporterMetrics.Dump());
                        }
                    }
                    catch(Exception e)
                    {
                        GlobalLog("An exception has occured: " + e.Message + "\n" + e.StackTrace);
                        throw;
                    }
                });
        }
        private string ManglePath(string path)
        {
            path = Path.GetFileNameWithoutExtension(path);
            int count = 0;
            fileNameDuplicateNumbering.TryGetValue(path, out count);
            fileNameDuplicateNumbering[path] = ++count;
            path += "_" + Convert.ToString(count);
            return path;
        }
        private static void UnpackCbdeExe()
        {
            GlobalLog("BeforePlatform GetExecutionAssembly");
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();

            GlobalLog("BeforePlatform detection");
            string res;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                res = "SonarAnalyzer.CBDE.windows.dotnet-symbolic-execution.exe";
                cbdeBinaryPath = Path.Combine(mlirProcessSpecificPath, "windows/dotnet-symbolic-execution.exe");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                res = "SonarAnalyzer.CBDE.linux.dotnet-symbolic-execution";
                cbdeBinaryPath = Path.Combine(mlirProcessSpecificPath, "linux/dotnet-symbolic-execution");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                res = "SonarAnalyzer.CBDE.macos.dotnet-symbolic-execution";
                cbdeBinaryPath = Path.Combine(mlirProcessSpecificPath, "macos/dotnet-symbolic-execution");
            }
            else
            {
                GlobalLog("Not a supported platform");
                throw new System.Exception("unsupported platform for CBDE");
            }
            GlobalLog(String.Format("Try Create Directory {0}", Path.GetDirectoryName(cbdeBinaryPath)));
            Directory.CreateDirectory(Path.GetDirectoryName(cbdeBinaryPath));
            GlobalLog("Create Directory OK");
            var stream = assembly.GetManifestResourceStream(res);
            GlobalLog("GetResourceStream OK");
            var fileStream = File.Create(cbdeBinaryPath);
            GlobalLog("Create exe file OK");
            stream.Seek(0, SeekOrigin.Begin);
            stream.CopyTo(fileStream);
            fileStream.Close();
            GlobalLog("Close exe file OK");
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    GlobalLog("Test OK");
                    var unixFileInfo = new Mono.Unix.UnixFileInfo(cbdeBinaryPath);
                    GlobalLog("New OK");
                    unixFileInfo.FileAccessPermissions |= Mono.Unix.FileAccessPermissions.OtherExecute;
                    unixFileInfo.FileAccessPermissions |= Mono.Unix.FileAccessPermissions.GroupExecute;
                    unixFileInfo.FileAccessPermissions |= Mono.Unix.FileAccessPermissions.UserExecute;
                    GlobalLog("Chmod OK");
                }
            }
            catch (Exception e)
            {
                // Something unexpected went wrong.
                GlobalLog(e.ToString());
                // Maybe it is also necessary to terminate / restart the application.
            }
        }
        private void InitializePathsAndLog(string assemblyName, int compilationHash)
        {
            SetupMlirRootDirectory();
            mlirDirectoryAssembly = Path.Combine(mlirDirectoryRoot, assemblyName, compilationHash.ToString());
            if (Directory.Exists(mlirDirectoryAssembly))
            {
                Directory.Delete(mlirDirectoryAssembly, true);
            }
            Directory.CreateDirectory(mlirDirectoryAssembly);
            cbdeJsonOutputPath = Path.Combine(mlirDirectoryAssembly, cbdeJsonOutputFileName);
            logFilePath = Path.Combine(mlirDirectoryAssembly, "CbdeHandler.log");
            logStream = new MemoryStream();
            logFile = new StreamWriter(logStream);
            logFile.WriteLine(">> New Cbde Run triggered at {0}", DateTime.Now.ToShortTimeString());
            logFile.Flush();
        }
        private void DumpLogToLogFile()
        {
            var str = UTF8Encoding.UTF8.GetString(logStream.GetBuffer());
            File.AppendAllText(logFilePath, str, Encoding.UTF8);
        }
        private void SetupMlirRootDirectory()
        {
            mlirDirectoryRoot = Path.Combine(mlirPath, "sonar-dotnet/cbde");
            Directory.CreateDirectory(mlirDirectoryRoot);
        }
        private void ExportFunctionMlir(SyntaxTree tree, SemanticModel model, MlirExporterMetrics exporterMetrics, string mlirFileName)
        {
            using (var mlirStreamWriter = new StreamWriter(Path.Combine(mlirDirectoryAssembly, mlirFileName)))
            {
                string perfLog = tree.GetRoot().GetLocation().GetLineSpan().Path + "\n";
                MLIRExporter mlirExporter = new MLIRExporter(mlirStreamWriter, model, exporterMetrics, true);
                foreach (var method in tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>())
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    mlirExporter.ExportFunction(method);
                    watch.Stop();

                    perfLog += method.Identifier + " " + watch.ElapsedMilliseconds + "\n";
                }
                PerformanceLog(perfLog + "\n");
            }
        }
        private void RunCbdeAndRaiseIssues(CompilationAnalysisContext c)
        {
            GlobalLog("Running CBDE");
            using (Process pProcess = new Process())
            {
                logFile.WriteLine("- Cbde process");
                pProcess.StartInfo.FileName = cbdeBinaryPath;
                pProcess.StartInfo.WorkingDirectory = mlirDirectoryAssembly;
                var progressLogFile = Path.Combine(mlirDirectoryAssembly, "progressLogFile.log");
                var cbdePerfLogFile = Path.Combine(mlirDirectoryAssembly, "perfLogFile.log");
                pProcess.StartInfo.Arguments = "-i " + "\"" + mlirDirectoryAssembly + "\" -o \"" + cbdeJsonOutputPath + "\" -p \"" + progressLogFile + "\" -s \"" + cbdePerfLogFile + "\"";
                logFile.WriteLine("  * binary_location: '{0}'", pProcess.StartInfo.FileName);
                logFile.WriteLine("  * arguments: '{0}'", pProcess.StartInfo.Arguments);
                pProcess.StartInfo.UseShellExecute = false;
                //pProcess.StartInfo.RedirectStandardOutput = true;
                pProcess.StartInfo.RedirectStandardError = true;
                pProcess.StartInfo.UseShellExecute = false;
                //pProcess.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                //pProcess.StartInfo.CreateNoWindow = true;
                logFile.Flush();
                pProcess.Start();
                pProcess.WaitForExit();
                //var output = pProcess.StandardOutput.ReadToEnd();
                //var eoutput = pProcess.StandardError.ReadToEnd();
                logFile.WriteLine("  * exit_code: '{0}'", pProcess.ExitCode);
                //logFile.WriteLine("  * stdout: '{0}'", output);
                //logFile.WriteLine("  * stderr: '{0}'", eoutput);
                logFile.Flush();
                // TODO: log this pProcess.PeakWorkingSet64;
                if (pProcess.ExitCode != 0)
                {
                    GlobalLog("Running CBDE: Failure");

                    var errorOutput = pProcess.StandardError.ReadToEnd();
                    File.AppendAllText(logFilePath, errorOutput, Encoding.UTF8);

                    RaiseIssueFromFailedCbdeRun(c);
                    //DumpLogToLogFile();
                    GlobalLog("Running CBDE: Error dumped");
                }
                else
                {
                    GlobalLog("Running CBDE: Success");
                    RaiseIssuesFromJSon(c);
                    Cleanup();
                    GlobalLog("Running CBDE: Issues reported");
                }
            }
        }
        private void Cleanup()
        {
            logFile.Dispose();
        }
        private void RaiseIssueFromJToken(JToken token, CompilationAnalysisContext context)
        {
            var key = token["key"].ToString();
            var message = token["message"].ToString();
            var location = token["location"];
            var line = Convert.ToInt32(location["l"]);
            var col = Convert.ToInt32(location["c"]);
            var file = location["f"].ToString();

            var begin = new LinePosition(line, col);
            var end = new LinePosition(line, col + 1);
            var loc = Location.Create(file, TextSpan.FromBounds(0, 0), new LinePositionSpan(begin, end));

            GlobalLog(String.Format("reporting from JToken: {0}: {1}", key, message));
            raiseIssue(key, message, loc, context);
        }
        private void RaiseIssueFromFailedCbdeRun(CompilationAnalysisContext context)
        {
            StringBuilder failureString = new StringBuilder("CBDE Failure Report :\n  C# souces files involved are:\n");
            foreach (var fileName in csSourceFileNames)
            {
                failureString.Append("  - " + fileName + "\n");
            }
            // we dispose the StreamWriter to unlock the log file
            failureString.Append("  content of the CBDE handler log file is :\n" + Encoding.UTF8.GetString(logStream.GetBuffer()));
            GlobalLog(failureString.ToString());
            Console.Error.WriteLine($"Error when executing MLIR, more details in {mlirProcessSpecificPath}");
        }
        private void RaiseIssuesFromJSon(CompilationAnalysisContext context)
        {
            string jsonFileContent;
            List<List<JObject>> jsonIssues;
            //logFile.WriteLine("- parsing json file {0}", cbdeJsonOutputPath);
            GlobalLog(String.Format("- parsing json file {0}", cbdeJsonOutputPath));
            try
            {
                jsonFileContent = File.ReadAllText(cbdeJsonOutputPath);
                jsonIssues = JsonConvert.DeserializeObject<List<List<JObject>>>(jsonFileContent);
            }
            catch(Exception e)
            {
                //logFile.WriteLine("- error parsing json file {0}", cbdeJsonOutputPath);
                GlobalLog(String.Format("- error parsing json file {0}: {1}", cbdeJsonOutputPath, e.Message));
                return;
            }

            foreach (var issue in jsonIssues.First())
            {
                //logFile.WriteLine("  * processing token {0}", issue.ToString());
                GlobalLog(String.Format("  * processing token {0}", issue.ToString()));
                try
                {
                    RaiseIssueFromJToken(issue, context);
                }
                catch
                {
                    //logFile.WriteLine("  * error reporting token {0}", cbdeJsonOutputPath);
                    GlobalLog(String.Format("  * error reporting token {0}", cbdeJsonOutputPath));
                    continue;
                }
            }
            logFile.Flush();
        }
    }
}