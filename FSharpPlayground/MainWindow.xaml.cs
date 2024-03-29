﻿using System;
using System.Collections.Generic;
using System.Drawing.Text;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using FSharp.Compiler.SourceCodeServices;
using System.Windows.Threading;
using Microsoft.FSharp.Core;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Windows.Interop;

namespace FSharpPlayground
{
    /// <summary>
    /// MainWindow.xaml 的交互逻辑
    /// </summary>
    public partial class MainWindow : SourceChord.FluentWPF.AcrylicWindow
    {
        const string fileFilter = 
            "F# Code (*.fs;*.fsx;*.fsscript)|*.fs;*.fsx;*.fsscript|F# Source Code (*.fs)|*.fs|F# Script (*.fsx;*.fsscript)|*.fsx;*.fsscript|All Files (*.*)|*.*";

        readonly string tempDir = Environment.GetEnvironmentVariable("TEMP") + "/";

        SourceChord.FluentWPF.ApplicationTheme currentTheme = (SourceChord.FluentWPF.ApplicationTheme)(-1);

        private void configHighlight()
        {
            if (currentTheme == SourceChord.FluentWPF.SystemTheme.AppTheme) return;
            currentTheme = SourceChord.FluentWPF.SystemTheme.AppTheme;

            using (var highlightxml = new StringReader(
                SynaxHighlight.Synax.Replace(
                    "<!-- __COLORS__ -->",
                    currentTheme == SourceChord.FluentWPF.ApplicationTheme.Dark ?
                        SynaxHighlight.DarkThemeColors : SynaxHighlight.LightThemeColors)))
            using (var r = new System.Xml.XmlTextReader(highlightxml))
                FSharpEditor.SyntaxHighlighting = ICSharpCode.AvalonEdit.Highlighting.Xshd.HighlightingLoader.Load(
                    r, ICSharpCode.AvalonEdit.Highlighting.HighlightingManager.Instance);
        }

        public MainWindow()
        {
            InitializeComponent();

            // 配置编辑器
            configHighlight();

            FSharpEditor.Options.ConvertTabsToSpaces = true;
            FSharpEditor.Options.EnableEmailHyperlinks = true;
            FSharpEditor.Options.EnableHyperlinks = true;
            FSharpEditor.Options.EnableImeSupport = true;
            FSharpEditor.Options.HighlightCurrentLine = true;

            Output.Options.EnableEmailHyperlinks = true;
            Output.Options.EnableHyperlinks = true;            

            // 读取设置
            Width = Settings.Default.WindowWidth;
            Height = Settings.Default.WindowHeight;
            editorWidthWhenOutputShown = Settings.Default.EditorWidth;
            FSharpEditor.Text = Settings.Default.Code;
            SetRunInNewConsole.IsChecked = Settings.Default.RunInNewConsole;
            SetUsingGlotIOAPI.IsChecked = Settings.Default.UsingGlotIO;
            Output.WordWrap = Settings.Default.WordWrapOutput;
            OutputWordWrapMenuItem.IsChecked = Output.WordWrap;
            if (Settings.Default.WindowMaxmized)
                WindowState = WindowState.Maximized;

            OutputWordWrapMenuItem.Checked += (e, o) => Output.WordWrap = true;
            OutputWordWrapMenuItem.Unchecked += (e, o) => Output.WordWrap = false;

            if (Environment.GetCommandLineArgs().Length == 2)
            {
                var file = Environment.GetCommandLineArgs()[1];
                if (File.Exists(file))
                    FSharpEditor.Text = File.ReadAllText(file);
            }

            // 写入设置
            Closing += (o, e) =>
            {
                KillTempExe();
                if (WindowState == WindowState.Maximized)
                    Settings.Default.WindowMaxmized = true;
                else
                {
                    Settings.Default.WindowMaxmized = false;
                    Settings.Default.WindowWidth = Width;
                    Settings.Default.WindowHeight = Height;
                }
                Settings.Default.EditorWidth = editorWidthWhenOutputShown;
                Settings.Default.Code = FSharpEditor.Text;
                Settings.Default.RunInNewConsole = SetRunInNewConsole.IsChecked;
                Settings.Default.UsingGlotIO = SetUsingGlotIOAPI.IsChecked;
                Settings.Default.WordWrapOutput = Output.WordWrap;
                Settings.Default.Save();

                CleanTempDir();
            };

            EditorOutputSplitter.DragCompleted += (e, o) =>
            {
                if(EditorOutputSplitter.IsVisible)
                    editorWidthWhenOutputShown = OutputCol.ActualWidth;
            };

            // 寻找字体
            InstalledFontCollection fonts = new InstalledFontCollection();
            foreach (var family in fonts.Families)
            {
                if(family.Name.StartsWith("Fira Code") || family.Name.StartsWith ("Envy Code"))
                {
                    FSharpEditor.FontFamily = new FontFamily(family.Name);
                    Output.FontFamily = new FontFamily(family.Name);
                    break;
                }
            }

            Loaded += (o, e) => 
                HwndSource.FromHwnd(new WindowInteropHelper(this).Handle).AddHook(new HwndSourceHook(WndProc));
        }

        bool storyEditorEnabled = true;
        private void SetEditorEnabled(bool enabled)
        {
            FSharpEditor.IsEnabled = enabled;
            NewButton.IsEnabled = enabled;
            OpenButton.IsEnabled = enabled;
            RunButton.IsEnabled = enabled;
            storyEditorEnabled = enabled;
        }

        private void SaveExe(object sender = null, RoutedEventArgs e = null)
        {
            using (var f = new System.Windows.Forms.SaveFileDialog()
            {
                Filter = "Executable File (*.exe)|*.exe|Dynamic Link Library (*.dll)|*.dll"
            })
            {
                f.FileOk += (s, e2) => {
                    Output.Clear();
                    SetEditorEnabled(false);
                    var src = FSharpEditor.Text;

                    new System.Threading.Thread(() =>
                    {
                        CompileToExe(f.FileName, src,true,f.FileName.ToLower().EndsWith("dll") ? "library" : "exe");
                        if(File.Exists(f.FileName))
                        {
                            Dispatcher.Invoke(() => Output.AppendText("Saved to " + f.FileName));
                        }
                        Dispatcher.Invoke(() => SetEditorEnabled(true));
                        Dispatcher.Invoke(() =>
                        {
                            if (EditorOutputSplitter.Visibility != Visibility.Visible)
                                HideOrShowOutput();
                        });
                    }).Start();
                };

                f.ShowDialog();
            }
        }

        struct GlotIOResponse
        {
            public string stdout { get; set; }
            public string stderr { get; set; }
            public string error { get; set; }
        };

        private string RunInGlotIO(string src)
        {
            using (var http = new System.Net.Http.HttpClient())
            {
                const string auth = "3e0a4bed-18f8-430d-9fad-c17e18392687";
                http.DefaultRequestHeaders.Add("Authorization", "Token " + auth);
                const string url = "https://run.glot.io/languages/fsharp/latest";
                var json = new { files = new object[] { new { name = "main.fs", content = src } } };
                var jsonStr = Newtonsoft.Json.JsonConvert.SerializeObject(json);
                using (var content = new System.Net.Http.StringContent(jsonStr))
                {
                    content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("application/json");
                    var response = http.PostAsync(url, content);
                    var responseStr = response.Result.Content.ReadAsStringAsync().Result;
                    var responseJson = Newtonsoft.Json.JsonConvert.DeserializeObject<GlotIOResponse>(responseStr);
                    var sb = new StringBuilder();
                    if (!System.String.IsNullOrEmpty(responseJson.stdout)) sb.Append(responseJson.stdout);
                    if(!System.String.IsNullOrEmpty(responseJson.stderr))
                    {
                        if (sb.Length > 0) sb.AppendLine();
                        sb.Append(responseJson.stderr);
                    }
                    if (!System.String.IsNullOrEmpty(responseJson.error))
                    {
                        if (sb.Length > 0) sb.AppendLine();
                        sb.Append(responseJson.error);
                    }
                    return sb.ToString();
                }
            }
        }

        private void Run(object sender = null,RoutedEventArgs e = null)
        {
            if (!storyEditorEnabled) return;
            Output.Clear();

            SetEditorEnabled(false);
            var src = FSharpEditor.Text;

            var runInOutput = !SetRunInNewConsole.IsChecked;
            var runInGlotIO = SetUsingGlotIOAPI.IsChecked;

            if (EditorOutputSplitter.Visibility != Visibility.Visible && runInOutput)
                HideOrShowOutput();

            var orgRunButtoncontent = RunButton.Content;

            new System.Threading.Thread(() =>
            {
                var tempTarget = tempDir + "temp.exe";
                KillTempExe();

                if (runInGlotIO)
                {
                    try
                    {
                        var result = RunInGlotIO(src);
                        Dispatcher.Invoke(() => Output.Text = result);
                        Dispatcher.Invoke(() => SetEditorEnabled(true));
                        return;
                    }
                    catch (Exception)
                    {
                        Dispatcher.Invoke(() => Output.Clear());
                    }
                }

                CompileToExe(tempTarget, src);
                CopyDLLsToTemp();

                if (File.Exists(tempTarget))
                {
                    using (var process = new Process())
                    {
                        process.StartInfo.FileName = tempTarget;
                        process.StartInfo.WorkingDirectory = Environment.CurrentDirectory;
                        process.StartInfo.UseShellExecute = false;
                        process.StartInfo.CreateNoWindow = runInOutput;
                        process.StartInfo.RedirectStandardInput = false;
                        process.StartInfo.RedirectStandardOutput = runInOutput;
                        process.StartInfo.RedirectStandardError = runInOutput;
                        process.Start();

                        
                        // 设置“Run”按钮为“Kill”
                        Dispatcher.Invoke(() =>
                        {
                            RunButton.Click -= Run;
                            RunButton.Click += KillTempExe;
                            RunButton.Content = "Kill";
                            RunButton.IsEnabled = true;
                        });
                        if (runInOutput)
                        {
                            while (!process.HasExited)
                            {
                                var log = process.StandardOutput.Read();
                                if (log >= 0)
                                    Dispatcher.Invoke(() =>
                                    {
                                        Output.AppendText(((char)(log)).ToString());
                                    });
                                else
                                    break;
                            }
                        }

                        process.WaitForExit();

                        // 恢复“Run”按钮
                        Dispatcher.Invoke(() =>
                        {
                            RunButton.Click -= KillTempExe;
                            RunButton.Click += Run;
                            RunButton.Content = orgRunButtoncontent;
                        });

                        if (runInOutput)
                        {
                            var logRemainder = process.StandardOutput.ReadToEnd();
                            var err = process.StandardError.ReadToEnd();
                            Dispatcher.Invoke(() =>
                            {
                                Output.AppendText(logRemainder);
                                Output.AppendText(Environment.NewLine);
                                Output.AppendText(err);
                            });
                        }
                        process.Close();
                    }
                }
                else
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (EditorOutputSplitter.Visibility != Visibility.Visible && !runInOutput)
                            HideOrShowOutput();
                    });
                }

                File.Delete(tempTarget);
                Dispatcher.Invoke(() => SetEditorEnabled(true));
            }).Start();
        }

        static void KillTempExe(object sender = null,RoutedEventArgs e = null)
        {
            using (var process = new Process())
            {
                process.StartInfo.FileName = "taskkill.exe";
                process.StartInfo.Arguments = "-f -im temp.exe";
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;
                process.Start();
                process.WaitForExit();
                process.Close();
            }
        }

        List<string> waitForClean = new List<string>();
        void CompileToExe(string outputExe,string src,bool optimize = false,string target="exe")
        {

            var checker = FSharpChecker.Create(
                FSharpOption<int>.None,
                FSharpOption<bool>.None,
                FSharpOption<bool>.None,
                FSharpOption<FSharp.Compiler.ReferenceResolver.Resolver>.None,
                FSharpOption<FSharpFunc<Tuple<string, DateTime>, FSharpOption<Tuple<object, IntPtr, int>>>>.None,
                FSharpOption<bool>.None,
                FSharpOption<bool>.None,
                FSharpOption<bool>.None);

            var tempSource = Environment.GetEnvironmentVariable("TEMP") + "/temp.fs";

            var args = new List<string>
            {
                "fsc.exe",
                "--nologo",
                "--crossoptimize" + (optimize ? "+" : "-"),
                "--optimize" + (optimize ? "+" : "-"),
                "-o", outputExe,
                "--target:"+target
            };

            if (optimize)
            {
                args.Add("--standalone");
            }

            var lines = new List<string>();
            var loads = new List<string>();
            var libDirs = new List<string>();
            // 提取#r、#I和#load
            using (var reader = new StringReader(src))
            {
                var references = new List<string>();
                
                while (reader.Peek() > 0)
                {
                    var line = reader.ReadLine();
                    var lineTrimed = line.Trim();

                    if (lineTrimed.StartsWith("#r "))
                    {
                        references.Add(lineTrimed.Substring(2).Trim().Trim('\"').Trim());
                        lines.Add("");
                    }
                    else if (lineTrimed.StartsWith("#I "))
                    {
                        libDirs.Add("\"" + lineTrimed.Substring(2).Trim().Trim('\"').Trim() + "\"");
                        lines.Add("");
                    }
                    else if (lineTrimed.StartsWith("#load "))
                    {
                        loads.Add(lineTrimed.Substring(5).Trim().Trim('\"').Trim());
                        lines.Add("");
                    }
                    else
                        lines.Add(line);
                }

                if(libDirs.Count > 0)
                {
                    args.Add("-I:" + libDirs.Aggregate((a, b) =>
                    {
                        return a + "," + b;
                    }));
                }

                if (references.Count > 0)
                {
                    if (!optimize)  // 因为在选定Optimized选项时已经具有了standalone选项，所以在此处需要补一个standalone选项
                    {
                        args.Add("--standalone");
                    }

                    foreach (var r in references)
                    {
                        args.Add("-r:" + r);

                        // 搜索dll并复制
                        var path = r.Trim().Trim('\"').Trim();
                        var n = new FileInfo(path).Name;
                        if (!File.Exists(tempDir + n))
                        {
                            if (!File.Exists(path))
                            {
                                foreach (var dir in libDirs)
                                {
                                    var s = dir.Trim().Trim('\"').Trim() + "/" + path;
                                    if (File.Exists(s))
                                    {
                                        path = s;
                                        break;
                                    }
                                }
                            }

                            if (File.Exists(path))
                            {
                                File.Copy(path, tempDir + n);
                                waitForClean.Add(n);
                            }
                        }
                    }
                }
            }

            foreach(var load in loads)
            {
                var fileName = load;
                if (!File.Exists(fileName))
                {
                    foreach (var i in libDirs)
                    {
                        var s = i.Trim().Trim('\"').Trim() + "/" + fileName;
                        if (File.Exists(s))
                        {
                            fileName = s;
                            break;
                        }
                    }
                }
                var warpedFileName = "\"" + fileName + "\"";
                args.Add(warpedFileName);
            }

            File.WriteAllLines(tempSource, lines);
            args.Add("\"" + tempSource + "\"");

            var async = checker.Compile(
                args.ToArray(),
                FSharpOption<string>.None);

            var result = Microsoft.FSharp.Control.FSharpAsync.RunSynchronously(
                async,
                FSharpOption<int>.None,
                FSharpOption<System.Threading.CancellationToken>.None);

            File.Delete(tempSource);

            if (result.Item1.Length > 0)
                Dispatcher.Invoke(() =>
                {
                    var sb = new StringBuilder();
                    foreach (var i in result.Item1)
                    {
                        sb
                            .Append('(')
                            .Append(i.Range.StartLine)
                            .Append(") ")
                            .Append("FS")
                            .Append(string.Format("{0:D4}", i.ErrorNumber))
                            .Append(':')
                            .AppendLine(i.Message);
                    }
                    Output.Text = sb.ToString();
                });
        }

        private void NewDocument(object sender = null, RoutedEventArgs e = null)
        {
            FSharpEditor.Text = Settings.Default.CodeTemplate;
        }

        private void SaveAsTemplate(object sender = null, RoutedEventArgs e = null)
        {
            Settings.Default.CodeTemplate = FSharpEditor.Text;
            Settings.Default.Save();
        }

        private void OpenDocument(object sender = null, RoutedEventArgs e = null)
        {
            using(var f = new System.Windows.Forms.OpenFileDialog()
            {
                Filter = fileFilter,
                Multiselect = false,
                ReadOnlyChecked = true
            })
            {
                f.FileOk += (s,e2) => {
                    if (!e2.Cancel)
                        FSharpEditor.Text = System.IO.File.ReadAllText(f.FileName);
                };

                f.ShowDialog();
            }
        }

        private void SaveDocument(object sender = null, RoutedEventArgs e = null)
        {
            using (var f = new System.Windows.Forms.SaveFileDialog()
            {
                Filter = fileFilter
            })
            {
                f.FileOk += (s, e2) => {
                    if (!e2.Cancel)
                        File.WriteAllText(f.FileName, FSharpEditor.Text);
                };

                f.ShowDialog();
            }
        }

        private double editorWidthWhenOutputShown;
        private void HideOrShowOutput(object sender = null, RoutedEventArgs e = null)
        {
            if(EditorOutputSplitter.Visibility == Visibility.Visible)
            {
                editorWidthWhenOutputShown = OutputCol.ActualWidth;
                OutputCol.MinWidth = 0;
                OutputCol.Width = new GridLength(0);
                EditorOutputSplitterCol.Width = new GridLength(0);
                EditorOutputSplitter.Visibility = Visibility.Collapsed;
                Output.Visibility = Visibility.Collapsed;
                HideOrShowOutputButton.Content = "Show Output";
            }
            else
            {
                var w = editorWidthWhenOutputShown;
                EditorOutputSplitterCol.Width = new GridLength(3);
                OutputCol.MinWidth = 100;
                OutputCol.Width = new GridLength(w);
                EditorOutputSplitter.Visibility = Visibility.Visible;
                Output.Visibility = Visibility.Visible;
                HideOrShowOutputButton.Content = "Hide Output";
            }
        }

        void CopyOutput(object sender, RoutedEventArgs e) => Output.Copy();
        void SelectAllOutput(object sender, RoutedEventArgs e) => Output.SelectAll();

        private void AcrylicContextMenu_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            var multiCharacterSelected = FSharpEditor.SelectionLength > 0;

            ContextMenuBlockComment.IsEnabled = multiCharacterSelected;

            ContextMenuUndo.IsEnabled = FSharpEditor.CanUndo;
            ContextMenuRedo.IsEnabled = FSharpEditor.CanRedo;

            ContextMenuCut.IsEnabled =
                ContextMenuCopy.IsEnabled =
                ContextMenuDel.IsEnabled = multiCharacterSelected;
        }

        private void SelectAll(object sender, RoutedEventArgs e) => FSharpEditor.SelectAll();
        private void Del(object sender, RoutedEventArgs e) => FSharpEditor.Delete();
        private void Paste(object sender, RoutedEventArgs e) => FSharpEditor.Paste();
        private void Copy(object sender, RoutedEventArgs e) => FSharpEditor.Copy();
        private void Cut(object sender, RoutedEventArgs e) => FSharpEditor.Cut();
        private void Undo(object sender, RoutedEventArgs e) => FSharpEditor.Undo();
        private void Redo(object sender, RoutedEventArgs e) => FSharpEditor.Redo();

        private void LineComment(object sender, RoutedEventArgs e)
        {
            if (!storyEditorEnabled) return;
            var beginLine = FSharpEditor.Document.GetLineByOffset(FSharpEditor.SelectionStart);
            var endLine = FSharpEditor.Document.GetLineByOffset(FSharpEditor.SelectionStart + FSharpEditor.SelectionLength);

            var currentLine = beginLine;
            do
            {
                var tail = FSharpEditor.Text.Substring(currentLine.Offset, currentLine.Length);
                if(!tail.TrimStart().StartsWith("//"))
                { 
                    FSharpEditor.Document.Insert(currentLine.Offset, "// ");
                }

                currentLine = currentLine.NextLine;
                if (currentLine == null) break;
            } while (currentLine.LineNumber <= endLine.LineNumber);

            FSharpEditor.SelectionStart = beginLine.Offset;
            FSharpEditor.SelectionLength = endLine.EndOffset - beginLine.Offset;
        }

        private void BlockComment(object sender,RoutedEventArgs e)
        {
            if (!storyEditorEnabled) return;
            var endPos = FSharpEditor.SelectionStart + FSharpEditor.SelectionLength;
            FSharpEditor.Document.Insert(endPos, "*)");
            FSharpEditor.Document.Insert(FSharpEditor.SelectionStart, "(*");
            FSharpEditor.Select(FSharpEditor.SelectionStart - 2, FSharpEditor.SelectionLength + 4);
        }

        private void SaveOutputAs(object sender,RoutedEventArgs e)
        {
            using (var f = new System.Windows.Forms.SaveFileDialog()
            {
                Filter = "All Files (*.*) | *.*"
            })
            {
                f.FileOk += (s, e2) => {
                    if (!e2.Cancel)
                        File.WriteAllText(f.FileName, Output.Text);
                };

                f.ShowDialog();
            }
        }


        private void CopyDLLsToTemp()
        {
            var fileInfo = new FileInfo(System.AppDomain.CurrentDomain.SetupInformation.ApplicationBase + "FSharp.Core.dll");
            if(!File.Exists(tempDir + fileInfo.Name))
                fileInfo.CopyTo(tempDir + fileInfo.Name);
        }

        private void CleanTempDir()
        {
            File.Delete(tempDir + "FSharp.Core.dll");
            File.Delete(tempDir + "temp.exe");

            foreach (var i in waitForClean)
                File.Delete(tempDir + i);
            waitForClean.Clear();
        }

        private void SetUsingGlotIOAPI_Checked(object sender, RoutedEventArgs e)
        {
            SetRunInNewConsole.IsEnabled = false;
            SetRunInNewConsole.IsChecked = false;
        }

        private void SetUsingGlotIOAPI_Unchecked(object sender, RoutedEventArgs e)
        {
            SetRunInNewConsole.IsEnabled = true;
        }
    }
}
