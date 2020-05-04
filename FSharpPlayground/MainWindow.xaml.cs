using System;
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

            // 读取设置
            Width = Settings.Default.WindowWidth;
            Height = Settings.Default.WindowHeight;
            editorWidthWhenOutputShown = Settings.Default.EditorWidth;
            FSharpEditor.Text = Settings.Default.Code;

            // 写入设置
            Closing += (o, e) =>
            {
                Settings.Default.WindowWidth = Width;
                Settings.Default.WindowHeight = Height;
                Settings.Default.EditorWidth = editorWidthWhenOutputShown.GetValueOrDefault(Width / 2);
                Settings.Default.Code = FSharpEditor.Text;
                Settings.Default.Save();
            };

            EditorCol.Width = new GridLength(Width);

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

        private void SetEditorEnabled(bool enabled)
        {
            FSharpEditor.IsEnabled = enabled;
            NewButton.IsEnabled = enabled;
            OpenButton.IsEnabled = enabled;
            RunButton.IsEnabled = enabled;
            SaveExeButton.IsEnabled = enabled;
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
            Output.Clear();
            Output.SetResourceReference(ForegroundProperty, "SystemBaseHighColorBrush");

            SetEditorEnabled(false);
            var src = FSharpEditor.Text;

            new System.Threading.Thread(() =>
            {
                var tempTarget = Environment.GetEnvironmentVariable("TEMP") + "/temp.exe";
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
                        process.WaitForExit();
                        var log = process.StandardOutput.ReadToEnd();
                        Dispatcher.Invoke(() => Output.Text = log);
                        process.Close();
                    }
                }

                File.Delete(tempTarget);
                Dispatcher.Invoke(() => SetEditorEnabled(true));
                Dispatcher.Invoke(() =>
                {
                    if (EditorOutputSplitter.Visibility != Visibility.Visible)
                        HideOrShowOutput();
                });
            }).Start();
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

        private double? editorWidthWhenOutputShown;
        private void HideOrShowOutput(object sender = null, RoutedEventArgs e = null)
        {
            if(EditorOutputSplitter.Visibility == Visibility.Visible)
            {
                editorWidthWhenOutputShown = EditorCol.ActualWidth;
                EditorCol.Width = new GridLength(EditorGrid.ActualWidth);
                EditorOutputSplitter.Visibility = Visibility.Collapsed;
                Output.Visibility = Visibility.Collapsed;
                HideOrShowOutputButton.Content = "Show Output";
            }
            else
            {
                var w = editorWidthWhenOutputShown.GetValueOrDefault(EditorGrid.ActualWidth / 2);
                EditorCol.Width = new GridLength(w);
                if (w >= Width * 0.9)
                {
                    EditorCol.Width = new GridLength(Width / 2);
                    editorWidthWhenOutputShown = Width / 2;
                }
                EditorOutputSplitter.Visibility = Visibility.Visible;
                Output.Visibility = Visibility.Visible;
                HideOrShowOutputButton.Content = "Hide Output";
            }
        }
    }
}
