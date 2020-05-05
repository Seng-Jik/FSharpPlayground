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

namespace FSharpPlayground
{
    /// <summary>
    /// MainWindow.xaml 的交互逻辑
    /// </summary>
    public partial class MainWindow : SourceChord.FluentWPF.AcrylicWindow
    {
        const string fileFilter = 
            "F# Code (*.fs;*.fsx)|*.fs;*.fsx|F# Source Code (*.fs)|*.fs|F# Script (*.fsx)|*.fsx|All Files (*.*)|*.*";

        public MainWindow()
        {
            InitializeComponent();

            // 配置编辑器
            using (var highlightxml = new StringReader(
                SynaxHighlight.Synax.Replace(
                    "<!-- __COLORS__ -->",
                    SourceChord.FluentWPF.SystemTheme.AppTheme == SourceChord.FluentWPF.ApplicationTheme.Dark ?
                        SynaxHighlight.DarkThemeColors : SynaxHighlight.LightThemeColors)))
            using (var r = new System.Xml.XmlTextReader(highlightxml))
                FSharpEditor.SyntaxHighlighting = ICSharpCode.AvalonEdit.Highlighting.Xshd.HighlightingLoader.Load(
                    r, ICSharpCode.AvalonEdit.Highlighting.HighlightingManager.Instance);

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
                Settings.Default.WindowWidth = Width;
                Settings.Default.WindowHeight = Height;
                Settings.Default.EditorWidth = editorWidthWhenOutputShown;
                Settings.Default.Code = FSharpEditor.Text;
                Settings.Default.Save();
            };

            // 寻找Fira Code字体
            InstalledFontCollection fonts = new InstalledFontCollection();
            foreach (var family in fonts.Families)
            {
                if(family.Name.StartsWith("Fira Code"))
                {
                    FSharpEditor.FontFamily = new FontFamily(family.Name);
                    Output.FontFamily = new FontFamily(family.Name);
                    break;
                }
            }
        }

        bool storyEditorEnabled = true;
        private void SetEditorEnabled(bool enabled)
        {
            FSharpEditor.IsEnabled = enabled;
            NewButton.IsEnabled = enabled;
            OpenButton.IsEnabled = enabled;
            RunButton.IsEnabled = enabled;
            SaveExeButton.IsEnabled = enabled;
            storyEditorEnabled = enabled;
        }

        private void SaveExe(object sender = null, RoutedEventArgs e = null)
        {
            using (var f = new System.Windows.Forms.SaveFileDialog()
            {
                Filter = "Executable File (*.exe)|*.exe"
            })
            {
                f.FileOk += (s, e2) => {
                    Output.Clear();
                    Output.SetResourceReference(ForegroundProperty, "SystemBaseHighColorBrush");
                    SetEditorEnabled(false);
                    var src = FSharpEditor.Text;

                    new System.Threading.Thread(() =>
                    {
                        CompileToExe(f.FileName, src);
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

        private void Run(object sender = null,RoutedEventArgs e = null)
        {
            if (!storyEditorEnabled) return;
            Output.Clear();
            Output.SetResourceReference(ForegroundProperty, "SystemBaseHighColorBrush");

            SetEditorEnabled(false);
            var src = FSharpEditor.Text;

            if (EditorOutputSplitter.Visibility != Visibility.Visible)
                HideOrShowOutput();


            new System.Threading.Thread(() =>
            {
                var tempTarget = Environment.GetEnvironmentVariable("TEMP") + "/temp.exe";
                KillTempExe();
                CompileToExe(tempTarget, src);

                if (File.Exists(tempTarget))
                {
                    using (var process = new Process())
                    {
                        process.StartInfo.FileName = tempTarget;
                        process.StartInfo.UseShellExecute = false;
                        process.StartInfo.CreateNoWindow = true;
                        process.StartInfo.RedirectStandardInput = false;
                        process.StartInfo.RedirectStandardOutput = true;
                        process.StartInfo.RedirectStandardError = true;
                        process.Start();
                        while (!process.HasExited)
                        {
                            var log = process.StandardOutput.ReadLine();
                            Dispatcher.Invoke(() => {
                                Output.AppendText(log);
                                Output.AppendText(Environment.NewLine);
                            });
                        }
                        process.WaitForExit();
                        var logRemainder = process.StandardOutput.ReadToEnd();
                        Dispatcher.Invoke(() => Output.AppendText(logRemainder));
                        process.Close();
                    }
                }

                File.Delete(tempTarget);
                Dispatcher.Invoke(() => SetEditorEnabled(true));
            }).Start();
        }

        static void KillTempExe()
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

        void CompileToExe(string outputExe,string src)
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


            File.WriteAllText(tempSource, src);


            var async = checker.Compile(
                new string[] { "fsc.exe", "-a", tempSource, "--nologo", "-O", "-o", outputExe, "--target:exe", "--standalone" },
                FSharpOption<string>.None);

            var result = Microsoft.FSharp.Control.FSharpAsync.RunSynchronously(
                async,
                FSharpOption<int>.None,
                FSharpOption<System.Threading.CancellationToken>.None);

            File.Delete(tempSource);

            if (result.Item1.Length > 0)
                Dispatcher.Invoke(() =>
                {
                    Output.Foreground = Brushes.Red;

                    var sb = new StringBuilder();
                    foreach (var i in result.Item1)
                    {
                        sb
                            .Append('(')
                            .Append(i.Range.StartLine)
                            .Append(") ")
                            .Append("error FS")
                            .Append(string.Format("{0:D4}", i.ErrorNumber))
                            .Append(':')
                            .AppendLine(i.Message);
                    }
                    Output.Text = sb.ToString();
                });
        }

        private void NewDocument(object sender = null, RoutedEventArgs e = null)
        {
            FSharpEditor.Clear();
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
    }
}
