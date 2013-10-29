#region License
/* **********************************************************************************
 * Copyright (c) Robert Nees 
 * This source code is subject to terms and conditions of the MIT License
 * for Irony. A copy of the license can be found in the License.txt file
 * at the root of this distribution.
 * By using this source code in any fashion, you are agreeing to be bound by the terms of the
 * MIT License.
 * You must not remove this notice from this software.
 * **********************************************************************************/
//Original Windows.Forms Version by Roman Ivantsov
//with contributions by Andrew Bradnan and Alexey Yakovlev
#endregion
using Gtk;
using Gdk;
using GLib;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Text;
using System.Diagnostics;
using System.IO;
using System.Configuration;
using System.Text.RegularExpressions;
using System.Xml;
using Irony.Ast;
using Irony.Parsing;
using Irony.GrammerExplorer;

//using Irony.GrammarExplorer.Highlighter;
namespace Irony.GrammarExplorer
{
	using ScriptException = Irony.Interpreter.ScriptException;

	// Settings conflict with Gtk.Settings
	using MyApp = Irony.GrammarExplorer.Properties;

	//that's the only place we use stuff from Irony.Interpreter
	public partial class MainWindow: Gtk.Window
	{
		public MainWindow () : base (Gtk.WindowType.Toplevel)
		{
//		Mono.TextEditor.TextEditor txTest = new Mono.TextEditor.TextEditor();
			Build ();
			// Add TextEditor to ScrolledWindow control (widget).
//		textEditorScrolledWindow.Child = textEditor;
//		textEditor.ShowAll ();
//		sWinTest.Child = txTest;
//		txTest.ShowAll ();

			// Setup GTK Models(listStore) as no way to due this in MonoDevelop/Stetic, so dumb...yuk....
			SetupModel_gridGrammarErrors ();
			SetupModel_gridCompileErrors ();
			SetupModel_cboGrammars ();
			SetModel_gridParserTrace ();
			SetModel_lstTokens ();
			SetupModel_btnManageGrammars ();

			tabGrammar.CurrentPage = 0;
			tabBottom.CurrentPage = 0;
			fmExploreGrammarWindowLoad ();
			this.Present ();
		}

		protected void OnDeleteEvent (object sender, DeleteEventArgs a)
		{
			fmExploreGrammarWindowClosing ();
			Application.Quit ();
			a.RetVal = true;
		}
		//fields
		Regex regexCleanWhiteSpace = new Regex (@"[ ]{2,}", RegexOptions.None);
		bool _fullScreen;
		Grammar _grammar;
		LanguageData _language;
		Parser _parser;
		ParseTree _parseTree;
		ScriptException _runtimeError;
		GrammarLoader _grammarLoader = new GrammarLoader ();
		bool _loaded;
		bool _treeClickDisabled;
		//to temporarily disable tree click when we locate the node programmatically

		private void SetupModel_gridGrammarErrors ()
		{
			// err.Level.ToString(), err.Message, err.State)
			gridGrammarErrors.AppendColumn ("Error Level", new Gtk.CellRendererText (), "text", 0);
			gridGrammarErrors.AppendColumn ("Description", new Gtk.CellRendererText (), "text", 1);
			gridGrammarErrors.AppendColumn ("Parse State", new Gtk.CellRendererText (), "text", 2);
			ListStore modelGridGrammarErrors = new ListStore (typeof(string), typeof(string), typeof(string));
			gridGrammarErrors.Model = modelGridGrammarErrors;
		}

		private void SetupModel_gridCompileErrors ()
		{
			// err.Level.ToString(), err.Message, err.State)
			gridCompileErrors.AppendColumn ("L. C", new Gtk.CellRendererText (), "text", 0);
			gridCompileErrors.AppendColumn ("Error Message", new Gtk.CellRendererText (), "text", 1);
			ListStore modelGridCompileErrors = new ListStore (typeof(string), typeof(string), typeof(string));
			gridCompileErrors.Model = modelGridCompileErrors;
		}

		private void SetupModel_cboGrammars ()
		{
			// Setup the combobox to handle storing/display of GrammerItem class
			ListStore listStore = new Gtk.ListStore (typeof(GrammarItem), typeof(string));
			cboGrammars.Model = listStore;
			CellRendererText text = new CellRendererText (); 
			cboGrammars.PackStart (text, false); 
			cboGrammars.AddAttribute (text, "text", 1); 
		}

		private void SetModel_gridParserTrace ()
		{
			// Note: 5th column (non-displayed) of ListStore contains the foreground color so we can highlight errors the 'easy' way in NodeView
			// err.Level.ToString(), err.Message, err.State)
			ListStore modelGridParserTrace = new ListStore (typeof(string), typeof(string), typeof(string), typeof(string), typeof(string));
			TreeViewColumn col = gridParserTrace.AppendColumn ("State", new Gtk.CellRendererText (), "text", 0, "foreground", 4);
			col.Alignment = 0.5f;
			col.FixedWidth = 60;
			col = gridParserTrace.AppendColumn ("Stack Top", new Gtk.CellRendererText (), "text", 1, "foreground", 4);
			col.Alignment = 0.5f;
			col.Expand = true;
			col.FixedWidth = 200;
			col.MaxWidth = 200;
			col = gridParserTrace.AppendColumn ("Input", new Gtk.CellRendererText (), "text", 2, "foreground", 4);
			col.Alignment = 0.5f;
			col.Expand = true;
			col.FixedWidth = 200;
			col = gridParserTrace.AppendColumn ("Action", new Gtk.CellRendererText (), "text", 3, "foreground", 4);
			col.Alignment = 0.5f;
			col.Expand = true;
			col.FixedWidth = 500;
			gridParserTrace.Model = modelGridParserTrace;
		}

		private void SetModel_lstTokens ()
		{
			ListStore modelLstTokens = new ListStore (typeof(string));
			lstTokens.AppendColumn ("Tokens", new Gtk.CellRendererText (), "text", 0, "foreground");
			lstTokens.Model = modelLstTokens;
		}

		// btnManageGrammars
		private void SetupModel_btnManageGrammars ()
		{
			// Setup the combobox to handle storing/display of Grammar Options and related function calls
			ListStore listStore = new Gtk.ListStore (typeof(string), typeof(Delegate));
			listStore.AppendValues("Load Grammars...",null);
			listStore.AppendValues("Remove Selected",null);
			listStore.AppendValues("Remove All",null);
			btnManageGrammars.Model = listStore;
			CellRendererText text = new CellRendererText (); 
			btnManageGrammars.PackStart (text, false); 
			// Only display the text column, not the function column
			btnManageGrammars.AddAttribute (text, "text", 0); 
		}

		#region Form load/unload events
		private void fmExploreGrammarWindowLoad ()
		{
			ClearLanguageInfo ();
			try {
				txtSource.Buffer.Text = MyApp.Settings.Default.SourceSample;
				txtSearch.Text = MyApp.Settings.Default.SearchPattern;
				GrammarItemList grammars = GrammarItemList.FromXml (MyApp.Settings.Default.Grammars);
				UpdateModelFromGrammerList (grammars, cboGrammars.Model as ListStore);
				chkParserTrace.Active = MyApp.Settings.Default.EnableTrace;
				chkDisableHili.Active = MyApp.Settings.Default.DisableHili;
				chkAutoRefresh.Active = MyApp.Settings.Default.AutoRefresh;

				//this will build parser and start colorizer
				TreeIter ti;
				cboGrammars.Model.GetIterFromString (out ti, MyApp.Settings.Default.LanguageIndex);
				if (!ti.Equals (null)) {
					cboGrammars.SetActiveIter (ti);
				}
			} catch {
			}
			_loaded = true;
		}

		private void fmExploreGrammarWindowClosing ()
		{
			MyApp.Settings.Default.SourceSample = txtSource.Buffer.Text;
			MyApp.Settings.Default.SearchPattern = txtSearch.Text;
			MyApp.Settings.Default.EnableTrace = chkParserTrace.Active;
			MyApp.Settings.Default.DisableHili = chkDisableHili.Active;
			MyApp.Settings.Default.AutoRefresh = chkAutoRefresh.Active;
			TreeIter ti;
			cboGrammars.GetActiveIter (out ti);
			MyApp.Settings.Default.LanguageIndex = cboGrammars.Model.GetStringFromIter (ti);

			Console.Write (cboGrammars.Model.GetStringFromIter (ti));
			var grammars = GetGrammarListFromModel (cboGrammars.Model as ListStore);
			MyApp.Settings.Default.Grammars = grammars.ToXml ();
			MyApp.Settings.Default.Save ();
		}

		private void UpdateModelFromGrammerList (GrammarItemList list, ListStore listStore)
		{
			// Following crashes when not on main Window thread, which makes sense:
			// listStore.Clear();
			// But even when using Gtk.Application.Invoke (delegate {}); to do it on the main UI thread... 
			// crash on GTK+ delegate, so hack it for now.
			ListStore newlistStore = new Gtk.ListStore (typeof(GrammarItem), typeof(string));
			cboGrammars.Model = newlistStore;
			// prevent the hack from leaking memory
			listStore.Dispose ();

			foreach (GrammarItem item in list) {
				newlistStore.AppendValues (item, item.Caption);
			}
		}

		private GrammarItemList GetGrammarListFromModel (ListStore listStore)
		{
			GrammarItemList list = new GrammarItemList ();
			foreach (Array item in listStore) {
				list.Add (item.GetValue (0) as GrammarItem);
			}
			return list;
		}
		#endregion

		#region Parsing and running

		private void CreateGrammar ()
		{
			_grammar = _grammarLoader.CreateGrammar ();
		}

		private void CreateParser ()
		{
			StopHighlighter ();
			btnRun.Sensitive = false;
			txtOutput.Buffer.Text = string.Empty;
			_parseTree = null;

			btnRun.Sensitive = _grammar is ICanRunSample;
			_language = new LanguageData (_grammar);
			_parser = new Parser (_language);
			ShowParserConstructionResults ();
			StartHighlighter ();
		}

		private void ParseSample ()
		{
			ClearParserOutput ();
			if (_parser == null || !_parser.Language.CanParse ())
				return;
			_parseTree = null;
//			GC.Collect (); //to avoid disruption of perf times with occasional collections
			_parser.Context.TracingEnabled = chkParserTrace.Active;
			try {
				_parser.Parse (txtSource.Buffer.Text, "<source>");
			} catch (Exception ex) {
				(gridCompileErrors.Model as ListStore).AppendValues (null, ex.Message, null);
				tabBottom.CurrentPage = 2; //pageParserOutput;
				throw;
			} finally {
				_parseTree = _parser.Context.CurrentParseTree;
				ShowCompilerErrors ();
				if (chkParserTrace.Active) {
					ShowParseTrace ();
				}
				ShowCompileStats ();
				ShowParseTree ();
				ShowAstTree ();
			}
		}

		private void RunSample ()
		{
			ClearRuntimeInfo ();
			Stopwatch sw = new Stopwatch ();
			int oldGcCount;
			txtOutput.Buffer.Text = "";
			try {
				if (_parseTree == null)
					ParseSample ();
				if (_parseTree.ParserMessages.Count > 0)
					return;

				System.GC.Collect (); //to avoid disruption of perf times with occasional collections
				oldGcCount = System.GC.CollectionCount (0);
				System.Threading.Thread.Sleep (100);

				sw.Start ();
				var iRunner = _grammar as ICanRunSample;
				var args = new RunSampleArgs (_language, txtSource.Buffer.Text, _parseTree);
				string output = iRunner.RunSample (args);
				sw.Stop ();
//				lblRunTime.Text = sw.ElapsedMilliseconds.ToString();
				var gcCount = System.GC.CollectionCount (0) - oldGcCount;
//				lblGCCount.Text = gcCount.ToString();
				WriteOutput (output);
				tabBottom.CurrentPage = 4; //pageOutput;
			} catch (ScriptException ex) {
				ShowRuntimeError (ex);
			} finally {
				sw.Stop ();
			}//finally
		}

		private void WriteOutput (string text)
		{
			if (string.IsNullOrEmpty (text))
				return;
			txtOutput.Buffer.Text += text + Environment.NewLine;
//			txtOutput.Buffer.SelectRange( Select(txtOutput.Text.Length - 1, 0);
		}

		#endregion

		#region Show... methods

		private void ClearLanguageInfo ()
		{
			lblLanguage.Text = string.Empty;
			lblLanguageVersion.Text = string.Empty;
			lblLanguageDescr.Text = string.Empty;
			txtGrammarComments.Buffer.Text = string.Empty;
		}

		private void ClearParserOutput ()
		{
			lblSrcLineCount.Text = string.Empty;
			lblSrcTokenCount.Text = "";
			lblParseTime.Text = "";
			lblParseErrorCount.Text = "";

			(lstTokens.Model as ListStore).Clear ();
			(gridCompileErrors.Model as ListStore).Clear ();
			(gridParserTrace.Model as ListStore).Clear ();
//			tvParseTree.Nodes.Clear();
//			tvAst.Nodes.Clear();
//			Application.DoEvents();
		}

		private void ShowLanguageInfo ()
		{
			if (_grammar == null)
				return;
			var langAttr = LanguageAttribute.GetValue (_grammar.GetType ());
			if (langAttr == null)
				return;
			lblLanguage.Text = langAttr.LanguageName;
			lblLanguageVersion.Text = langAttr.Version;
			lblLanguageDescr.Text = langAttr.Description;
			txtGrammarComments.Buffer.Text = _grammar.GrammarComments;
		}

		private void ShowCompilerErrors ()
		{
			(gridCompileErrors.Model as ListStore).Clear ();
			if (_parseTree == null || _parseTree.ParserMessages.Count == 0)
				return;
			foreach (var err in _parseTree.ParserMessages)
				(gridCompileErrors.Model as ListStore).AppendValues (err.Location.ToUiString (), err.Message, err.ParserState.ToString ());
			var needPageSwitch = tabBottom.CurrentPage != 2 && //  pageParserOutput
			                     !(tabBottom.CurrentPage == 3 && chkParserTrace.Active);
			if (needPageSwitch)
				tabBottom.CurrentPage = 2; // pageParserOutput;
		}

		private void ShowParseTrace ()
		{
			(gridParserTrace.Model as ListStore).Clear ();
			String cellColor;
			foreach (ParserTraceEntry entry in _parser.Context.ParserTrace) {
				if (entry.IsError) {
					cellColor = "red";
				} else {
					cellColor = "black";
				}
				// Getting strange Application/GTK crashes with NO/NONE/ZERO stack/exception details from assignment of some parse tree data (?) when 'AppendValues'.
				// (gridParserTrace.Model as ListStore).AppendValues((entry.State.Name), (entry.StackTop.ToString()), (entry.Input.ToString()), (entry.Message), cellColor );

				// MORE INFO: 'sometimes' GTK# is reporting (I REALLY HATE GTK# 2.1.2.... !!!!!!!):
				//   System.String[]System.String[]System.String[]System.String[]Marshaling clicked signal
				//		Exception in Gtk# callback delegate
				//			Note: Applications can use GLib.ExceptionManager.UnhandledException to handle the exception.
				// So 'bad' strings are being passed: GTK#->GTK/Glib
				// Much later....Found it...
				// Glib crash of accessing entry.Input when null;
				// Does not cause problems in Trace, Watch, Local, etc... but Mono/Glib fault
				// Why are they marshaling nulls??? and why does it fault Glib?
				string inputNoNullString = "";
				if (entry.Input != null) {
					inputNoNullString = regexCleanWhiteSpace.Replace (entry.Input.ToString (), @" ");
				}
				//TODO
				string cleanedup = regexCleanWhiteSpace.Replace (entry.StackTop.ToString (), @" ");

//				string cleanedup = System.Text.RegularExpressions.Regex.Replace(entry.StackTop.ToString(),@"\s+"," ");
				(gridParserTrace.Model as ListStore).AppendValues ((entry.State.Name), (cleanedup), inputNoNullString, (entry.Message), cellColor);

//				(gridParserTrace.Model as ListStore).AppendValues(foobar[0], foobar[1], foobar[2], foobar[3], cellColor);
			}
			//Show tokens
			String foo;
			foreach (Token tkn in _parseTree.Tokens) {
				if (chkExcludeComments.Active && tkn.Category == TokenCategory.Comment)
					continue;
				foo = tkn.ToString ();
				//TODO
//				System.Environment.NewLine
//				foo = System.Text.RegularExpressions.Regex.Replace (foo.Trim(), @"\n+", " ");
				string cleanedup = System.Text.RegularExpressions.Regex.Replace (foo, @"\s+", " ");
				(lstTokens.Model as ListStore).AppendValues (cleanedup);
			}
		}

		private void ShowCompileStats ()
		{
			if (_parseTree != null) {
				lblSrcLineCount.Text = string.Empty;
				if (_parseTree.Tokens.Count > 0)
					lblSrcLineCount.Text = (_parseTree.Tokens [_parseTree.Tokens.Count - 1].Location.Line + 1).ToString ();
				lblSrcTokenCount.Text = _parseTree.Tokens.Count.ToString ();
				lblParseTime.Text = _parseTree.ParseTimeMilliseconds.ToString ();
				lblParseErrorCount.Text = _parseTree.ParserMessages.Count.ToString ();
//	     		Application.DoEvents();
			}
			//Note: this time is "pure" parse time; actual delay after cliking "Compile" includes time to fill ParseTree, AstTree controls
		}

		private void ShowParseTree ()
		{
//			tvParseTree.Nodes.Clear();
//			if (_parseTree == null) return;
//			AddParseNodeRec(null, _parseTree.Root);
		}

		private void AddParseNodeRec (TreeNode parent, ParseTreeNode node)
		{
//			if (node == null) return;
//			string txt = node.ToString();
//			TreeNode tvNode = (parent == null? tvParseTree.Nodes.Add(txt) : parent.Nodes.Add(txt) );
//			tvNode.Tag = node;
//			foreach(var child in node.ChildNodes)
//				AddParseNodeRec(tvNode, child);
		}

		private void ShowAstTree ()
		{
//			tvAst.Nodes.Clear();
//			if (_parseTree == null || _parseTree.Root == null || _parseTree.Root.AstNode == null) return;
//			AddAstNodeRec(null, _parseTree.Root.AstNode);
		}

		private void AddAstNodeRec (TreeNode parent, object astNode)
		{
//			if (astNode == null) return;
//			string txt = astNode.ToString();
//			TreeNode newNode = (parent == null ?
//			                    tvAst.Nodes.Add(txt) : parent.Nodes.Add(txt));
//			newNode.Tag = astNode;
//			var iBrowsable = astNode as IBrowsableAstNode;
//			if (iBrowsable == null) return;
//			var childList = iBrowsable.GetChildNodes();
//			foreach (var child in childList)
//				AddAstNodeRec(newNode, child);
		}

		private void ShowParserConstructionResults ()
		{
			lblParserStateCount.Text = _language.ParserData.States.Count.ToString ();
			lblParserConstrTime.Text = _language.ConstructionTime.ToString ();
			(gridGrammarErrors.Model as ListStore).Clear ();
			txtTerms.Buffer.Text = string.Empty;
			txtNonTerms.Buffer.Text = string.Empty;
			txtParserStates.Buffer.Text = string.Empty;
			tabBottom.CurrentPage = 0; // pageLanguage;
			if (_parser == null)
				return;
			txtTerms.Buffer.Text = ParserDataPrinter.PrintTerminals (_language);
			txtNonTerms.Buffer.Text = ParserDataPrinter.PrintNonTerminals (_language);
			txtParserStates.Buffer.Text = ParserDataPrinter.PrintStateList (_language);
			ShowGrammarErrors ();
		}
		//method
		private void ShowGrammarErrors ()
		{
			(gridGrammarErrors.Model as ListStore).Clear ();
			var errors = _parser.Language.Errors;
			if (errors.Count == 0)
				return;
			foreach (var err in errors)
				(gridGrammarErrors.Model as ListStore).AppendValues (err.Level.ToString (), err.Message, err.State);
			if (tabBottom.CurrentPage != 1) // pageGrammarErrors
				tabBottom.CurrentPage = 0;
		}

		private void ShowSourcePosition (int position, int length)
		{
//			if (position < 0) return;
//			txtSource.SelectionStart = position;
//			txtSource.SelectionLength = length;
//			//txtSource.Select(location.Position, length);
//			txtSource.DoCaretVisible();
//			if (tabGrammar.SelectedTab != pageTest)
//				tabGrammar.SelectedTab = pageTest;
//			txtSource.Focus();
//			//lblLoc.Text = location.ToString();
		}

		private void ShowSourcePositionAndTraceToken (int position, int length)
		{
//			ShowSourcePosition(position, length);
//			//find token in trace
//			for (int i = 0; i < lstTokens.Items.Count; i++) {
//				var tkn = lstTokens.Items[i] as Token;
//				if (tkn.Location.Position == position) {
//					lstTokens.SelectedIndex = i;
//					return;
//				}//if
//			}//for i
		}

		private void LocateParserState (ParserState state)
		{
//			if (state == null) return;
//			if (tabGrammar.SelectedTab != pageParserStates)
//				tabGrammar.SelectedTab = pageParserStates;
//			//first scroll to the bottom, so that scrolling to needed position brings it to top
//			txtParserStates.SelectionStart = txtParserStates.Text.Length - 1;
//			txtParserStates.ScrollToCaret();
//			DoSearch(txtParserStates, "State " + state.Name, 0);
		}

		private void ShowRuntimeError (ScriptException error)
		{
//			_runtimeError = error;
//			lnkShowErrLocation.Enabled = _runtimeError != null;
//			lnkShowErrStack.Enabled = lnkShowErrLocation.Enabled;
//			if (_runtimeError != null) {
//				//the exception was caught and processed by Interpreter
//				WriteOutput("Error: " + error.Message + " At " + _runtimeError.Location.ToUiString() + ".");
//				ShowSourcePosition(_runtimeError.Location.Position, 1);
//			} else {
//				//the exception was not caught by interpreter/AST node. Show full exception info
//				WriteOutput("Error: " + error.Message);
//				fmShowException.ShowException(error);
//
//			}
//			tabBottom.SelectedTab = pageOutput;
		}

		private void SelectTreeNode (TreeView tree, TreeNode node)
		{
//			_treeClickDisabled = true;
//			tree.SelectedNode = node;
//			if (node != null)
//				node.EnsureVisible();
//			_treeClickDisabled = false;
		}

		private void ClearRuntimeInfo ()
		{
//			lnkShowErrLocation.Enabled = false;
//			lnkShowErrStack.Enabled = false;
//			_runtimeError = null;
//			txtOutput.Text = string.Empty;
		}

		#endregion

		#region Grammar combo menu commands

		private void menuGrammars_Opening (object sender, CancelEventArgs e)
		{
//			miRemove.Enabled = cboGrammars.Items.Count > 0;
		}

		private void miAdd_Click (object sender, EventArgs e)
		{
//			if (dlgSelectAssembly.ShowDialog() != DialogResult.OK) return;
//			string location = dlgSelectAssembly.FileName;
//			if (string.IsNullOrEmpty(location)) return;
//			var oldGrammars = new GrammarItemList();
//			foreach(var item in cboGrammars.Items)
//				oldGrammars.Add((GrammarItem) item);
//			var grammars = fmSelectGrammars.SelectGrammars(location, oldGrammars);
//			if (grammars == null) return;
//			foreach (GrammarItem item in grammars)
//				cboGrammars.Items.Add(item);
//			btnRefresh.Enabled = false;
//			// auto-select the first grammar if no grammar currently selected
//			if (cboGrammars.SelectedIndex < 0 && grammars.Count > 0)
//				cboGrammars.SelectedIndex = 0;
		}

		private void miRemove_Click (object sender, EventArgs e)
		{
//			if (MessageBox.Show("Are you sure you want to remove grammmar " + cboGrammars.SelectedItem + "?",
//			                    "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes) {
//				cboGrammars.Items.RemoveAt(cboGrammars.SelectedIndex);
//				_parser = null;
//				if (cboGrammars.Items.Count > 0)
//					cboGrammars.SelectedIndex = 0;
//				else
//					btnRefresh.Enabled = false;
//			}
		}

		private void miRemoveAll_Click (object sender, EventArgs e)
		{
//			if (MessageBox.Show("Are you sure you want to remove all grammmars in the list?",
//			                    "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes) {
//				cboGrammars.Items.Clear();
//				btnRefresh.Enabled = false;
//				_parser = null;
//			}
		}
		#endregion

		private void SelectGrammarAssembly()
		{
			Gtk.FileChooserDialog fc=
				new Gtk.FileChooserDialog("Choose the Irony-based grammar to open",
				                          this,
				                          FileChooserAction.Open,
				                          "Cancel",ResponseType.Cancel,
				                          "Open",ResponseType.Accept);
			fc.Run ();
			string location = fc.Filename;
			if (!string.IsNullOrEmpty (location)) {
				var oldGrammars = new GrammarItemList ();
				fc.Destroy ();
				SelectGrammars (location, oldGrammars);
			} else {
				fc.Destroy ();
			}
		}

//		protected void OnBtnManageGrammarsClicked (object sender, EventArgs e)
//		{
////			Gtk.FileChooserDialog fc=
////				new Gtk.FileChooserDialog("Choose the file to open",
////				                          this,
////				                          FileChooserAction.Open,
////				                          "Cancel",ResponseType.Cancel,
////				                          "Open",ResponseType.Accept);
////			fc.Run ();
//			m.ShowNow ();
//
//			//Propagate the event
////			Event evnt = Gtk.Global.CurrentEvent;
////			Gtk.Global.PropagateEvent(dlgSelectAssembly, evnt);
////			dlgSelectAssembly.Run ();
////			dlgSelectAssembly.Activate ();
////			dlgSelectAssembly.ButtonPressEvent (dlgSelectAssembly, new ButtonPressEventArgs ());
//			Console.WriteLine ("OnDlgSelectAssemblySelectionChanged");
////			OnDlgSelectAssemblySelectionChanged (dlgSelectAssembly, new EventArgs ());
//////			GLib.Signal.Emit (dlgSelectAssembly, "button-press-event");
//
//		}

		protected void OnBtnRefreshClicked (object sender, EventArgs e)
		{
			LoadSelectedGrammar ();
		}

		protected void OnSWinTestAdded (object o, AddedArgs args)
		{
//			throw new NotImplementedException ();
		}

		protected void OnBtnLocateClicked (object sender, EventArgs e)
		{
//			throw new NotImplementedException ();
		}

		protected void OnChkAutoRefreshClicked (object sender, EventArgs e)
		{
//			throw new NotImplementedException ();
		}

		protected void OnBtnRefreshEntered (object sender, EventArgs e)
		{
//			throw new NotImplementedException ();
		}

		protected void OnTxtSearchTextDeleted (object o, TextDeletedArgs args)
		{
//			throw new NotImplementedException ();
		}

		protected void SelectGrammars (string filename, GrammarItemList grammerlist)
		{
//			var grammars = fmSelectGrammars.SelectGrammars (filename, grammerlist);
//			if (grammars != null) {
//				// Store the Grammer items from the dll in the combobox ListStore model
//				foreach (GrammarItem item in grammars)
//					(cboGrammars.Model as ListStore).AppendValues (item, item.Caption);
//
//				btnRefresh.Sensitive = false;
//
//				// auto-select the first grammar if no grammar currently selected
////				TreeIter ti;
////				cboGrammars.Model.GetIterFirst (out ti);
////				cboGrammars.SetActiveIter (ti);
//			}

			dlgSelectGrammars foo = new dlgSelectGrammars ();
			foo.Parent = this;
			foo.WindowPosition = WindowPosition.CenterOnParent;
			foo.ShowGrammars (filename, grammerlist, new dlgSelectGrammars.ProcessGrammars(foobar));
		}

		public delegate void ProcessBookDelegate(GrammarItemList grammarlist);
		public void foobar (GrammarItemList grammarlist) {
			Console.WriteLine ("callback");
			if (grammarlist != null) {
				// Store the Grammer items from the dll in the combobox ListStore model
				UpdateModelFromGrammerList (grammarlist, cboGrammars.Model as ListStore);
				btnRefresh.Sensitive = false;
				// auto-select the first grammar if no grammar currently selected
				TreeIter ti;
				cboGrammars.Model.GetIterFirst (out ti);
				cboGrammars.SetActiveIter (ti);
			}
		}

		protected void OnDlgSelectAssemblySelectionChanged (object sender, EventArgs e)
		{
			FileChooserButton file = sender as FileChooserButton;
			string location = file.Filename;
			if (string.IsNullOrEmpty (location))
				return;
			var oldGrammars = new GrammarItemList ();
			SelectGrammars (location, oldGrammars);
		}

		#region miscellaneous: LoadSourceFile, Search, Source highlighting

		private void LoadSourceFile (string path)
		{
			_parseTree = null;
			StreamReader reader = null;
			try {
				reader = new StreamReader (path);
//				txtSource.Buffer.Text = null;  //to clear any old formatting
//				txtSource.ClearUndo();
//				txtSource.ClearStylesBuffer();
//				txtSource.Text = reader.ReadToEnd();
//				txtSource.SetVisibleState(0, FastColoredTextBoxNS.VisibleState.Visible);
//				txtSource.Selection = txtSource.GetRange(0, 0);
			} catch (Exception e) {
//				MessageBox.Show(e.Message);
			} finally {
				if (reader != null)
					reader.Close ();
			}
		}
		//Source highlighting
		//		FastColoredTextBoxHighlighter _highlighter;
		private void StartHighlighter ()
		{
//			if (_highlighter != null)
//				StopHighlighter();
			if (chkDisableHili.Activate ())
				return;
			if (!_parser.Language.CanParse ())
				return;
//TODO
//			_highlighter = new FastColoredTextBoxHighlighter(txtSource, _language);
//			_highlighter.Adapter.Activate();
		}

		private void StopHighlighter ()
		{
//			if (_highlighter == null) return;
//			_highlighter.Dispose();
//			_highlighter = null;
			ClearHighlighting ();
		}

		private void ClearHighlighting ()
		{
//			var selectedRange = txtSource.Selection;
//			var visibleRange = txtSource.VisibleRange;
//			var firstVisibleLine = Math.Min(visibleRange.Start.iLine, visibleRange.End.iLine);
//
//			var txt = txtSource.Buffer.Text;
//			txtSource.Clear();
//			txtSource.Buffer.Text = txt; //remove all old highlighting
//
//			txtSource.SetVisibleState(firstVisibleLine, FastColoredTextBoxNS.VisibleState.Visible);
//			txtSource.Selection = selectedRange;
		}

		private void EnableHighlighter (bool enable)
		{
//			if (_highlighter != null)
//				StopHighlighter();
//			if (enable)
//				StartHighlighter();
		}
		//The following methods are contributed by Andrew Bradnan; pasted here with minor changes
		private void DoSearch ()
		{
//			lblSearchError.Visible = false;
//			TextBoxBase textBox = GetSearchContentBox();
//			if (textBox == null) return;
//			int idxStart = textBox.SelectionStart + textBox.SelectionLength;
//			if (!DoSearch(textBox, txtSearch.Text, idxStart)) {
//				lblSearchError.Text = "Not found.";
//				lblSearchError.Visible = true;
//			}
		}
		//		private bool DoSearch(TextBoxBase textBox, string fragment, int start) {
		//			textBox.SelectionLength = 0;
		//			// Compile the regular expression.
		//			Regex r = new Regex(fragment, RegexOptions.IgnoreCase);
		//			// Match the regular expression pattern against a text string.
		//			Match m = r.Match(textBox.Text.Substring(start));
		//			if (m.Success) {
		//				int i = 0;
		//				Group g = m.Groups[i];
		//				CaptureCollection cc = g.Captures;
		//				Capture c = cc[0];
		//				textBox.SelectionStart = c.Index + start;
		//				textBox.SelectionLength = c.Length;
		//				textBox.Focus();
		//				textBox.ScrollToCaret();
		//				return true;
		//			}
		//			return false;
		//		}//method
		//		public TextBoxBase GetSearchContentBox() {
		//			switch (tabGrammar.SelectedIndex) {
		//			case 0:
		//				return txtTerms;
		//			case 1:
		//				return txtNonTerms;
		//			case 2:
		//				return txtParserStates;
		//				//case 4:
		//				//  return txtSource;
		//			default:
		//				return null;
		//			}//switch
		//		}

		#endregion

		#region Controls event handlers

		//		private void btnParse_Click(object sender, EventArgs e) {
		//			ParseSample();
		//		}
		protected void OnBtnParseClicked (object sender, EventArgs e)
		{
			ParseSample ();
		}
		//		private void btnRun_Click(object sender, EventArgs e) {
		//			RunSample();
		//		}
		protected void OnBtnRunClicked (object sender, EventArgs e)
		{
			RunSample ();
		}
		//		private void tvParseTree_AfterSelect(object sender, TreeViewEventArgs e) {
		//			if (_treeClickDisabled)
		//				return;
		//			var vtreeNode = tvParseTree.SelectedNode;
		//			if (vtreeNode == null) return;
		//			var parseNode = vtreeNode.Tag as ParseTreeNode;
		//			if (parseNode == null) return;
		//			ShowSourcePosition(parseNode.Span.Location.Position, 1);
		//		}
		//
		//		private void tvAst_AfterSelect(object sender, TreeViewEventArgs e) {
		//			if (_treeClickDisabled)
		//				return;
		//			var treeNode = tvAst.SelectedNode;
		//			if (treeNode == null) return;
		//			var iBrowsable = treeNode.Tag as IBrowsableAstNode;
		//			if (iBrowsable == null) return;
		//			ShowSourcePosition(iBrowsable.Position, 1);
		//		}
		bool _changingGrammar;

		private void LoadSelectedGrammar ()
		{
			try {
				ClearLanguageInfo ();
				ClearParserOutput ();
				ClearRuntimeInfo ();

				_changingGrammar = true;
				CreateGrammar ();
				ShowLanguageInfo ();
				CreateParser ();
			} finally {
				_changingGrammar = false; //in case of exception
			}
			btnRefresh.Sensitive = true;
		}
		//		private void cboGrammars_SelectedIndexChanged(object sender, EventArgs e) {
		//			_grammarLoader.SelectedGrammar = cboGrammars.SelectedItem as GrammarItem;
		//			LoadSelectedGrammar();
		//		}
		protected void OnCboGrammarsChanged (object sender, EventArgs e)
		{
			TreeIter ti;
			cboGrammars.GetActiveIter (out ti);
			_grammarLoader.SelectedGrammar = cboGrammars.Model.GetValue (ti, 0) as GrammarItem;
			LoadSelectedGrammar ();
		}

		private void GrammarAssemblyUpdated (object sender, EventArgs args)
		{
//			if (InvokeRequired) {
//				Invoke(new EventHandler(GrammarAssemblyUpdated), sender, args);
//				return;
//			}
//			if (chkAutoRefresh.Checked) {
//				LoadSelectedGrammar();
//				txtGrammarComments.Text += String.Format("{0}Grammar assembly reloaded: {1:HH:mm:ss}", Environment.NewLine, DateTime.Now);
//			}
		}

		private void btnRefresh_Click (object sender, EventArgs e)
		{
			LoadSelectedGrammar ();
		}
		//		private void btnFileOpen_Click(object sender, EventArgs e) {
		//			if (dlgOpenFile.ShowDialog() != DialogResult.OK) return;
		//			ClearParserOutput();
		//			LoadSourceFile(dlgOpenFile.FileName);
		//		}
		protected void OnFcbtnFileOpenSelectionChanged (object sender, EventArgs e)
		{
			FileChooserButton file = sender as FileChooserButton;
			string location = file.Filename;
			if (string.IsNullOrEmpty (location))
				return;
			ClearParserOutput ();
			LoadSourceFile (location);
		}
		//TODO
		//		private void txtSource_TextChanged(object sender, FastColoredTextBoxNS.TextChangedEventArgs e) {
		//			_parseTree = null; //force it to recompile on run
		//		}
		//		private void btnManageGrammars_Click(object sender, EventArgs e) {
		//			menuGrammars.Show(btnManageGrammars, 0, btnManageGrammars.Height);
		//		}
		private void cboParseMethod_SelectedIndexChanged (object sender, EventArgs e)
		{
			//changing grammar causes setting of parse method combo, so to prevent double-call to ConstructParser
			// we don't do it here if _changingGrammar is set
			if (!_changingGrammar)
				CreateParser ();
		}
		//		private void gridParserTrace_CellDoubleClick(object sender, DataGridViewCellEventArgs e) {
		//			if (_parser.Context == null || e.RowIndex < 0 || e.RowIndex >= _parser.Context.ParserTrace.Count) return;
		//			var entry = _parser.Context.ParserTrace[e.RowIndex];
		//			switch (e.ColumnIndex) {
		//			case 0: //state
		//			case 3: //action
		//				LocateParserState(entry.State);
		//				break;
		//			case 1: //stack top
		//				if (entry.StackTop != null)
		//					ShowSourcePositionAndTraceToken(entry.StackTop.Span.Location.Position, entry.StackTop.Span.Length);
		//				break;
		//			case 2: //input
		//				if (entry.Input != null)
		//					ShowSourcePositionAndTraceToken(entry.Input.Span.Location.Position, entry.Input.Span.Length);
		//				break;
		//			}//switch
		//		}
		//		private void lstTokens_Click(object sender, EventArgs e) {
		//			if (lstTokens.SelectedIndex < 0)
		//				return;
		//			Token token = (Token)lstTokens.SelectedItem;
		//			ShowSourcePosition(token.Location.Position, token.Length);
		//		}
		//		private void gridCompileErrors_CellDoubleClick(object sender, DataGridViewCellEventArgs e) {
		//			if (e.RowIndex < 0 || e.RowIndex >= gridCompileErrors.Rows.Count) return;
		//			var err = gridCompileErrors.Rows[e.RowIndex].Cells[1].Value as LogMessage;
		//			switch (e.ColumnIndex) {
		//			case 0: //state
		//			case 1: //stack top
		//				ShowSourcePosition(err.Location.Position, 1);
		//				break;
		//			case 2: //input
		//				if (err.ParserState != null)
		//					LocateParserState(err.ParserState);
		//				break;
		//			}//switch
		//		}
		//		private void gridGrammarErrors_CellDoubleClick(object sender, DataGridViewCellEventArgs e) {
		//			if (e.RowIndex < 0 || e.RowIndex >= gridGrammarErrors.Rows.Count) return;
		//			var state = gridGrammarErrors.Rows[e.RowIndex].Cells[2].Value as ParserState;
		//			if (state != null)
		//				LocateParserState(state);
		//		}
		//		private void btnSearch_Click(object sender, EventArgs e) {
		//			DoSearch();
		//		}//method
		protected void OnBtnSearchClicked (object sender, EventArgs e)
		{
			DoSearch ();
		}
		//		private void txtSearch_KeyPress(object sender, KeyPressEventArgs e) {
		//			if (e.KeyChar == '\r')  // <Enter> key
		//				DoSearch();
		//		}
		protected void OnTxtSearchActivated (object sender, EventArgs e)
		{
			DoSearch ();
		}
		//		private void lnkShowErrLocation_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e) {
		//			if (_runtimeError != null)
		//				ShowSourcePosition(_runtimeError.Location.Position, 1);
		//		}
		//		private void lnkShowErrStack_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e) {
		//			if (_runtimeError == null) return;
		//			if (_runtimeError.InnerException != null)
		//				fmShowException.ShowException(_runtimeError.InnerException);
		//			else
		//				fmShowException.ShowException(_runtimeError);
		//		}
		//		private void btnLocate_Click(object sender, EventArgs e) {
		//			if (_parseTree == null)
		//				ParseSample();
		//			var p = txtSource.SelectionStart;
		//			tvParseTree.SelectedNode = null; //just in case we won't find
		//			tvAst.SelectedNode = null;
		//			SelectTreeNode(tvParseTree, LocateTreeNode(tvParseTree.Nodes, p, node => (node.Tag as ParseTreeNode).Span.Location.Position));
		//			SelectTreeNode(tvAst, LocateTreeNode(tvAst.Nodes, p, node => (node.Tag as IBrowsableAstNode).Position));
		//			txtSource.Focus(); //set focus back to source
		//		}
		//		private TreeNode LocateTreeNode(TreeNodeCollection nodes, int position, Func<TreeNode, int> positionFunction) {
		//			TreeNode current = null;
		//			//Find the last node in the list that is "before or at" the position
		//			foreach (TreeNode node in nodes) {
		//				if (positionFunction(node) > position) break; //from loop
		//				current = node;
		//			}
		//			//if current has children, search them
		//			if (current != null && current.Nodes.Count > 0)
		//				current = LocateTreeNode(current.Nodes, position, positionFunction) ?? current;
		//			return current;
		//		}
		//

		private void chkDisableHili_CheckedChanged(object sender, EventArgs e) {
			if (!_loaded) return;
			EnableHighlighter(!chkDisableHili.Active);
		}

		protected void OnBtnRunActivated (object sender, EventArgs e)
		{
//			throw new NotImplementedException ();
		}

		protected void OnDefaultActivated (object sender, EventArgs e)
		{
			(sender as Gtk.Window).Present ();
		}

		protected void OnRealized (object sender, EventArgs e)
		{
			(sender as Gtk.Window).Present ();
		}

		protected void OnStateChanged (object sender, EventArgs e)
		{
			Debug.Write ("window state change");
			_fullScreen = !_fullScreen;
		}

		protected void OnQuitAction1Activated (object sender, EventArgs e)
		{
			Application.Quit ();
		}

		protected void OnMinimizeActionActivated (object sender, EventArgs e)
		{
			this.Iconify ();
		}

		protected void OnFullScreenActionActivated (object sender, EventArgs e)
		{
			if (!_fullScreen) {
				this.Fullscreen ();
//				FullScreenAction.Label = "Exit Full Screen";
			} else {
				this.Unfullscreen ();	
//				FullScreenAction.Label = "Enter Full Screen";
			}
			_fullScreen = !_fullScreen;
		}

		bool _InManageGrammarSelectiion = false; //hack, no context menu like winform, using a combobox instead.
		// and need to ignore 'second' change event when clearing the selection after processing user's selection
		protected void OnbtnManageGrammarsChanged (object sender, EventArgs e)
		{
			if (!_InManageGrammarSelectiion) {
				_InManageGrammarSelectiion = true;
				try {
					string _manageGrammar;
					TreeIter ti;
					btnManageGrammars.GetActiveIter (out ti);
					ListStore listStore = btnManageGrammars.Model as ListStore;
					_manageGrammar = listStore.GetValue (ti, 0) as string;
					Console.WriteLine(_manageGrammar);
					SelectGrammarAssembly ();
				} finally {
					btnManageGrammars.SetActiveIter (TreeIter.Zero);
					_InManageGrammarSelectiion = false;
				}
			}
		}
		#endregion

	}
}
