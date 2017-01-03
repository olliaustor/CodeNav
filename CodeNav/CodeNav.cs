﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using CodeNav.Mappers;
using CodeNav.Models;
using CodeNav.Properties;
using EnvDTE;
using Microsoft.VisualStudio.Text.Editor;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Window = EnvDTE.Window;

namespace CodeNav
{
    internal class CodeNav : DockPanel, IWpfTextViewMargin
    {
        public const string MarginName = "CodeNav";
        private bool _isDisposed;

        private CodeViewUserControl _codeViewUserControl;
        private readonly CodeDocumentViewModel _codeDocumentVm;
        private readonly DTE _dte;
        private readonly IWpfTextView _textView;
        private readonly DocumentEvents _documentEvents;
        private WindowEvents _windowEvents;
        private List<string> _highlightedItems;
        private readonly BackgroundWorker _backgroundWorker;
        private readonly Dictionary<string, List<CodeItem>> _cache;

        public CodeNav(IWpfTextViewHost textViewHost, DTE dte)
        {
            // If there are no code elements in the document, don't do anything
            if (dte.ActiveDocument?.ProjectItem?.FileCodeModel?.CodeElements == null) return;

            _highlightedItems = new List<string>();
            _codeDocumentVm = new CodeDocumentViewModel();
            _cache = new Dictionary<string, List<CodeItem>>();

            // Wire up references for the event handlers in RegisterEvents
            _dte = dte;
            _textView = textViewHost.TextView;
            _documentEvents = dte.Events.DocumentEvents;
               

            // Setup the backgroundworker that will map the document to the codeitems
            _backgroundWorker = new BackgroundWorker {WorkerSupportsCancellation = true};
            _backgroundWorker.DoWork += _backgroundWorker_DoWork;
            _backgroundWorker.RunWorkerCompleted += _backgroundWorker_RunWorkerCompleted;

            // Add the view/content to the margin area
            Children.Add(CreateGrid(textViewHost, dte));       
        }

        private void _backgroundWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            var stopwatch = new Stopwatch();
            stopwatch.Start();

            var areEqual = _codeDocumentVm.CodeDocument.SequenceEqual((List<CodeItem>)e.Result, new CodeItemComparer());
            if (areEqual)
            {
                stopwatch.Stop();
                Log($"RunWorkerCompleted in {stopwatch.ElapsedMilliseconds} ms, document did not change");
                return;
            }

            _codeDocumentVm.CodeDocument = (List<CodeItem>)e.Result;
            _cache[_dte.ActiveDocument.Path] = (List<CodeItem>)e.Result;
            ((Grid)Children[0]).ColumnDefinitions[0].Width = !_codeDocumentVm.CodeDocument.Any() ? new GridLength(0) : new GridLength(Settings.Default.Width);

            stopwatch.Stop();
            Log($"RunWorkerCompleted in {stopwatch.ElapsedMilliseconds} ms");
        }

        private void _backgroundWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            e.Result = CodeItemMapper.MapDocument((CodeElements)e.Argument);
        }

        public void RegisterEvents()
        {
            // Subscribe to Cursor move event
            if (_textView?.Caret != null)
            {
                _textView.Caret.PositionChanged -= Caret_PositionChanged;
                _textView.Caret.PositionChanged += Caret_PositionChanged;
            }

            // Subscribe to Document Save event
            if (_documentEvents != null)
            {
                _documentEvents.DocumentSaved -= DocumentEvents_DocumentSaved;
                _documentEvents.DocumentSaved += DocumentEvents_DocumentSaved;
            }

            // Subscribe to Code window activated event
            if (_dte?.ActiveDocument?.ActiveWindow == null) return;
            _windowEvents = _dte.Events.WindowEvents[_dte.ActiveDocument.ActiveWindow];
            _windowEvents.WindowActivated -= WindowEvents_WindowActivated;
            _windowEvents.WindowActivated += WindowEvents_WindowActivated;                           
        }

        public void UnRegisterEvents()
        {
            _textView.Caret.PositionChanged -= Caret_PositionChanged;
            _documentEvents.DocumentSaved -= DocumentEvents_DocumentSaved;
            _windowEvents.WindowActivated -= WindowEvents_WindowActivated;
        }

        private void DocumentEvents_DocumentSaved(Document document) => UpdateDocument(_dte.ActiveDocument.ActiveWindow);
        private void WindowEvents_WindowActivated(Window gotFocus, Window lostFocus) => UpdateDocument(gotFocus);
        private void Caret_PositionChanged(object sender, CaretPositionChangedEventArgs e) => UpdateCurrentItem();

        private void UpdateCurrentItem()
        {
            if (_dte?.ActiveDocument?.Selection == null || _codeDocumentVm?.CodeDocument == null) return;

            var textSelection = _dte.ActiveDocument.Selection as TextSelection;

            var currentFunctionElement = textSelection?.ActivePoint.CodeElement[vsCMElement.vsCMElementFunction];

            if (currentFunctionElement == null)
            {
                UnHighlight(_codeDocumentVm.CodeDocument, _highlightedItems);
                return;
            }

            UnHighlight(_codeDocumentVm.CodeDocument, _highlightedItems);

            _highlightedItems = new List<string>();
            GetItemsToHighlight(_highlightedItems, currentFunctionElement);

            Highlight(_codeDocumentVm.CodeDocument, _highlightedItems);         
        }

        private static void GetItemsToHighlight(List<string> list, CodeElement element)
        {
            list.Add(element.FullName);

            var parent = element.Collection.Parent;
            if (parent == null) return;

            GetItemsToHighlight(list, parent);
        }

        private static void UnHighlight(List<CodeItem> document, List<string> itemNames)
        {
            foreach (var name in itemNames)
            {
                var item = FindCodeItem(document, name);
                if (item == null) return;

                item.Foreground = new SolidColorBrush(Colors.Black);

                if (item is CodeClassItem)
                {
                    (item as CodeClassItem).BorderBrush = new SolidColorBrush(Colors.DarkGray);
                }
            }
        }

        private static void Highlight(List<CodeItem> document, List<string> itemNames)
        {
            foreach (var name in itemNames)
            {
                var item = FindCodeItem(document, name);
                if (item == null) return;

                item.Foreground = new SolidColorBrush(Colors.SteelBlue);

                if (item is CodeClassItem)
                {
                    (item as CodeClassItem).BorderBrush = new SolidColorBrush(Colors.SteelBlue);
                }
            }
        }

        private static CodeItem FindCodeItem(List<CodeItem> items, string itemFullName)
        {
            foreach (var item in items)
            {
                if (item.FullName.Equals(itemFullName))
                {
                    return item;
                }

                if (item is CodeClassItem)
                {
                    var classItem = (CodeClassItem)item;
                    if (classItem.Members.Any())
                    {
                        var found = FindCodeItem(classItem.Members, itemFullName);
                        if (found != null)
                        {
                            return found;
                        }
                    }
                }
            }
            return null;
        }

        private void UpdateDocument(Window gotFocus = null)
        {
            // Do we have a text document in the activated window
            if (gotFocus?.Document == null) return;

            // Do we have code items in the text document
            var elements = _dte.ActiveDocument?.ProjectItem?.FileCodeModel?.CodeElements;
            if (elements == null) return;

            // Do we have a cached version of this document
            if (_cache.ContainsKey(_dte.ActiveDocument.Path))
            {
                _codeDocumentVm.CodeDocument = _cache[_dte.ActiveDocument.Path];
            }

            // If not show a loading item
            if (_codeDocumentVm.CodeDocument == null)
            {
                _codeDocumentVm.CodeDocument = new List<CodeItem>
                {
                    new CodeClassItem
                    {
                        Name = "Loading...",
                        FullName = "Loading...",
                        Id = "Loading...",
                        Foreground = new SolidColorBrush(Colors.Black),
                        BorderBrush = new SolidColorBrush(Colors.DarkGray),
                        IconPath = "Icons/Refresh/Refresh_16x.xaml"
                    }
                };
            }

            // Is the backgroundworker already doing something, if so stop it
            if (_backgroundWorker.IsBusy)
            {
                _backgroundWorker.CancelAsync();
            }

            // Start the backgroundworker to update the list of code items
            if (!_backgroundWorker.CancellationPending)
            {
                _backgroundWorker.RunWorkerAsync(elements);
            }            
        }

        private Grid CreateGrid(IWpfTextViewHost textViewHost, DTE dte)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Settings.Default.Width, GridUnitType.Pixel) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5, GridUnitType.Pixel) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition());

            var splitter = new GridSplitter
            {
                Width = 5,
                ResizeDirection = GridResizeDirection.Columns,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            splitter.DragCompleted += LeftDragCompleted;
            grid.Children.Add(splitter);

            _codeViewUserControl = new CodeViewUserControl(dte) { DataContext = _codeDocumentVm };
            grid.Children.Add(_codeViewUserControl);

            Grid.SetColumn(_codeViewUserControl, 0);
            Grid.SetColumn(splitter, 1);
            Grid.SetColumn(textViewHost.HostControl, 2);

            return grid;
        }

        private void LeftDragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (!double.IsNaN(_codeViewUserControl.ActualWidth))
            {
                Settings.Default.Width = _codeViewUserControl.ActualWidth;
                Settings.Default.Save();
            }
        }

        private static void Log(string message)
        {
            #if DEBUG
                Logger.Log(message);
            #endif
        }

        #region IWpfTextViewMargin

        /// <summary>
        /// Gets the <see cref="FrameworkElement"/> that implements the visual representation of the margin.
        /// </summary>
        /// <exception cref="ObjectDisposedException">The margin is disposed.</exception>
        public FrameworkElement VisualElement
        {
            // Since this margin implements Canvas, this is the object which renders
            // the margin.
            get
            {
                ThrowIfDisposed();
                return this;
            }
        }

        #endregion

        #region ITextViewMargin

        /// <summary>
        /// Gets the size of the margin.
        /// </summary>
        /// <remarks>
        /// For a horizontal margin this is the height of the margin,
        /// since the width will be determined by the <see cref="ITextView"/>.
        /// For a vertical margin this is the width of the margin,
        /// since the height will be determined by the <see cref="ITextView"/>.
        /// </remarks>
        /// <exception cref="ObjectDisposedException">The margin is disposed.</exception>
        public double MarginSize
        {
            get
            {
                ThrowIfDisposed();

                // Since this is a horizontal margin, its width will be bound to the width of the text view.
                // Therefore, its size is its height.
                return ActualHeight;
            }
        }

        /// <summary>
        /// Gets a value indicating whether the margin is enabled.
        /// </summary>
        /// <exception cref="ObjectDisposedException">The margin is disposed.</exception>
        public bool Enabled
        {
            get
            {
                ThrowIfDisposed();

                // The margin should always be enabled
                return true;
            }
        }

        /// <summary>
        /// Gets the <see cref="ITextViewMargin"/> with the given <paramref name="marginName"/> or null if no match is found
        /// </summary>
        /// <param name="marginName">The name of the <see cref="ITextViewMargin"/></param>
        /// <returns>The <see cref="ITextViewMargin"/> named <paramref name="marginName"/>, or null if no match is found.</returns>
        /// <remarks>
        /// A margin returns itself if it is passed its own name. If the name does not match and it is a container margin, it
        /// forwards the call to its children. Margin name comparisons are case-insensitive.
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="marginName"/> is null.</exception>
        public ITextViewMargin GetTextViewMargin(string marginName)
        {
            return String.Equals(marginName, MarginName, StringComparison.OrdinalIgnoreCase) ? this : null;
        }

        /// <summary>
        /// Disposes an instance of <see cref="CodeNav"/> class.
        /// </summary>
        public void Dispose()
        {
            if (_isDisposed) return;
            UnRegisterEvents();
            GC.SuppressFinalize(this);
            _isDisposed = true;
        }

        #endregion

            /// <summary>
            /// Checks and throws <see cref="ObjectDisposedException"/> if the object is disposed.
            /// </summary>
        private void ThrowIfDisposed()
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException(MarginName);
            }
        }
    }
}
