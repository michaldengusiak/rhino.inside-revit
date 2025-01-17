using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

using DB = Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;

using Rhino;
using Rhino.PlugIns;

using Grasshopper;
using Grasshopper.Plugin;
using Grasshopper.Kernel;
using Grasshopper.GUI.Canvas;
using RhinoInside.Revit.External.DB.Extensions;
using RhinoInside.Revit.Convert.Units;
using System.Diagnostics;

namespace RhinoInside.Revit.GH
{
  [GuestPlugInId("B45A29B1-4343-4035-989E-044E8580D9CF")]
  class Guest : IGuest
  {
    #region IGuest
    PreviewServer previewServer;

    public string Name => "Grasshopper";

    internal static Guest Instance;

    public Guest()
    {
      if (Instance is object)
        throw new InvalidOperationException();

      Instance = this;
    }

    LoadReturnCode IGuest.OnCheckIn(ref string errorMessage)
    {
      string message = null;
      try
      {
        if (!LoadStartupAssemblies())
          message = "Failed to load Revit Grasshopper components.";
      }
      catch (FileNotFoundException e) { message = $"{e.Message}{Environment.NewLine}{e.FileName}"; }
      catch (Exception e)             { message = e.Message; }

      if (!(message is null))
      {
        errorMessage = message;
        return LoadReturnCode.ErrorShowDialog;
      }

      // Register PreviewServer
      previewServer = new PreviewServer();
      previewServer.Register();

      Revit.DocumentChanged += OnDocumentChanged;

      External.ActivationGate.Enter += ActivationGate_Enter;
      External.ActivationGate.Exit  += ActivationGate_Exit;

      RhinoDoc.BeginOpenDocument                += BeginOpenDocument;
      RhinoDoc.EndOpenDocumentInitialViewUpdate += EndOpenDocumentInitialViewUpdate;

      Instances.CanvasCreatedEventHandler Canvas_Created = default;
      Instances.CanvasCreated += Canvas_Created = (canvas) =>
      {
        Instances.CanvasCreated -= Canvas_Created;
        Instances.DocumentEditor.Activated += DocumentEditor_Activated;
        canvas.DocumentChanged             += ActiveCanvas_DocumentChanged;
      };

      Instances.CanvasDestroyedEventHandler Canvas_Destroyed = default;
      Instances.CanvasDestroyed += Canvas_Destroyed = (canvas) =>
      {
        Instances.CanvasDestroyed -= Canvas_Destroyed;
        canvas.DocumentChanged             -= ActiveCanvas_DocumentChanged;
        Instances.DocumentEditor.Activated -= DocumentEditor_Activated;
      };

      Instances.DocumentServer.DocumentAdded += DocumentServer_DocumentAdded;

      return LoadReturnCode.Success;
    }

    void IGuest.OnCheckOut()
    {
      Instances.DocumentServer.DocumentAdded -= DocumentServer_DocumentAdded;

      RhinoDoc.EndOpenDocumentInitialViewUpdate -= EndOpenDocumentInitialViewUpdate;
      RhinoDoc.BeginOpenDocument                -= BeginOpenDocument;

      External.ActivationGate.Exit  -= ActivationGate_Exit;
      External.ActivationGate.Enter -= ActivationGate_Enter;

      Revit.DocumentChanged -= OnDocumentChanged;

      // Unregister PreviewServer
      previewServer?.Unregister();
      previewServer = null;
    }
    #endregion

    #region Grasshopper Editor
    static readonly GH_RhinoScriptInterface Script = new GH_RhinoScriptInterface();

    /// <summary>
    /// Returns the loaded state of the Grasshopper Main window.
    /// </summary>
    /// <returns>True if the Main Grasshopper Window has been loaded.</returns>
    public static bool IsEditorLoaded() => Script.IsEditorLoaded();

    /// <summary>
    /// Load the main Grasshopper Editor. If the editor has already been loaded nothing
    /// will happen.
    /// </summary>
    public static void LoadEditor()
    {
      Script.LoadEditor();
      if (!Script.IsEditorLoaded())
        throw new Exception("Failed to startup Grasshopper");
    }

    /// <summary>
    /// Returns the visible state of the Grasshopper Main window.
    /// </summary>
    /// <returns>True if the Main Grasshopper Window has been loaded and is visible.</returns>
    public static bool IsEditorVisible() => Script.IsEditorVisible();

    /// <summary>
    /// Show the main Grasshopper Editor. The editor will be loaded first if needed.
    /// If the Editor is already on screen, nothing will happen.
    /// </summary>
    public static void ShowEditor()
    {
      Script.ShowEditor();
      Rhinoceros.MainWindow.BringToFront();
    }

    /// <summary>
    /// Show Grasshopper window asynchronously
    /// </summary>
    public static async void ShowEditorAsync()
    {
      // Yield execution back to Revit and show Grasshopper window asynchronously.
      await External.ActivationGate.Yield();

      ShowEditor();
    }

    /// <summary>
    /// Hide the main Grasshopper Editor. If the editor hasn't been loaded or if the
    /// Editor is already hidden, nothing will happen.
    /// </summary>
    public static void HideEditor() => Script.HideEditor();

    /// <summary>
    /// Open a Grasshopper document. The editor will be loaded if necessary, but it will not be automatically shown.
    /// </summary>
    /// <param name="filename">Path of file to open (must be a *.gh or *.ghx extension).</param>
    /// <returns>True on success, false on failure.</returns>
    public static bool OpenDocument(string filename) => Script.OpenDocument(filename);

    /// <summary>
    /// Open a Grasshopper document. The editor will be loaded and shown if necessary.
    /// </summary>
    /// <param name="filename">Full path to GH definition file</param>
    /// <param name="showEditor">True to force the Main Grasshopper Window visible.</param>
    public static async void OpenDocumentAsync(string filename, bool showEditor = true)
    {
      // Yield execution back to Revit and show Grasshopper window asynchronously.
      await External.ActivationGate.Yield();

      if (showEditor)
        ShowEditor();

      OpenDocument(filename);
    }

    void ActivationGate_Enter(object sender, EventArgs e)
    {
      if (Instances.ActiveCanvas?.Document is GH_Document definition)
        definition.Enabled = true;

      if (EnableSolutions.HasValue)
      {
        GH_Document.EnableSolutions = EnableSolutions.Value;
        EnableSolutions = null;
      }
    }

    void ActivationGate_Exit(object sender, EventArgs e)
    {
      if (Instances.ActiveCanvas?.Document is GH_Document definition)
        definition.Enabled = false;
    }

    static bool? EnableSolutions;
    private void DocumentServer_DocumentAdded(GH_DocumentServer sender, GH_Document doc)
    {
      if (!External.ActivationGate.IsOpen)
      {
        if (GH_Document.EnableSolutions)
        {
          GH_Document.EnableSolutions = false;
          EnableSolutions = true;
        }
      }
    }

    bool activeDefinitionWasEnabled = false;
    void BeginOpenDocument(object sender, DocumentOpenEventArgs e)
    {
      if (Instances.ActiveCanvas?.Document is GH_Document definition)
      {
        activeDefinitionWasEnabled = definition.Enabled;
        definition.Enabled = false;
      }
    }

    void EndOpenDocumentInitialViewUpdate(object sender, DocumentOpenEventArgs e)
    {
      if (Instances.ActiveCanvas?.Document is GH_Document definition)
      {
        definition.Enabled = activeDefinitionWasEnabled;
        definition.NewSolution(false);
      }
    }
    #endregion

    #region Grasshopper Assemblies
    static bool LoadGHA(string filePath)
    {
      var LoadGHAProc = typeof(GH_ComponentServer).GetMethod("LoadGHA", BindingFlags.NonPublic | BindingFlags.Instance);
      if (LoadGHAProc is null)
      {
        var message = new StringBuilder();
        message.AppendLine("An attempt is made to invoke an invalid target method.");
        message.AppendLine();
        var assembly = typeof(GH_ComponentServer).Assembly;
        var assemblyName = assembly.GetName();

        message.AppendLine($"Assembly Version={assemblyName.Version}");
        message.AppendLine($"{assembly.Location.Replace(' ', (char) 0xA0)}");

        throw new TargetException(message.ToString());
      }

      try
      {
        return (bool) LoadGHAProc.Invoke
        (
          Instances.ComponentServer,
          new object[] { new GH_ExternalFile(filePath), false }
        );
      }
      catch (TargetInvocationException e)
      {
        throw e.InnerException;
      }
    }

    static bool EnumGHLink(string filePath, List<string> files)
    {
      try
      {
        foreach (var fullLine in File.ReadAllLines(filePath))
        {
          var line = fullLine.Trim();
          if (string.IsNullOrEmpty(line)) continue;
          if (line.StartsWith("#")) continue;
          if (line.StartsWith("//")) continue;

          if (File.Exists(line)) files.Add(line);
          else if (Directory.Exists(line))
          {
            var folder = new DirectoryInfo(line);

            IEnumerable<FileInfo> assemblyFiles;
            try { assemblyFiles = folder.EnumerateFiles("*.gha"); }
            catch (System.IO.DirectoryNotFoundException) { continue; }

            foreach (var assemblyFile in assemblyFiles)
            {
              // https://docs.microsoft.com/en-us/dotnet/api/system.io.directory.enumeratefiles?view=netframework-4.8
              // If the specified extension is exactly three characters long,
              // the method returns files with extensions that begin with the specified extension.
              // For example, "*.xls" returns both "book.xls" and "book.xlsx"
              if (assemblyFile.Extension.ToLower() != ".gha") continue;

              files.Add(assemblyFile.FullName);
            }
          }
        }
      }
      catch { return false; }

      return true;
    }

    static List<string> GetAssembliesList()
    {
      DirectoryInfo[] DefaultAssemblyFolders =
      {
        // %ProgramData%\Grasshopper\Libraries-Inside-Revit-20XX
        new DirectoryInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Grasshopper", $"Libraries-{Rhinoceros.SchemeName}")),

        // %APPDATA%\Grasshopper\Libraries-Inside-Revit-20XX
        new DirectoryInfo(Folders.DefaultAssemblyFolder.Substring(0, Folders.DefaultAssemblyFolder.Length - 1) + '-' + Rhinoceros.SchemeName)
      };

      var map = new System.Collections.Specialized.OrderedDictionary();

      foreach (var folder in DefaultAssemblyFolders)
      {
        IEnumerable<FileInfo> assemblyFiles;
        try { assemblyFiles = folder.EnumerateFiles("*.gha", SearchOption.AllDirectories); }
        catch (System.IO.DirectoryNotFoundException) { continue; }

        foreach (var assemblyFile in assemblyFiles)
        {
          // https://docs.microsoft.com/en-us/dotnet/api/system.io.directory.enumeratefiles?view=netframework-4.8
          // If the specified extension is exactly three characters long,
          // the method returns files with extensions that begin with the specified extension.
          // For example, "*.xls" returns both "book.xls" and "book.xlsx"
          if (assemblyFile.Extension.ToLower() != ".gha") continue;

          var key = assemblyFile.FullName.Substring(folder.FullName.Length);
          if (map.Contains(key))
            map.Remove(key);

          map.Add(key, assemblyFile);
        }

        IEnumerable<FileInfo> linkFiles;
        try { linkFiles = folder.EnumerateFiles("*.ghlink", SearchOption.TopDirectoryOnly); }
        catch (System.IO.DirectoryNotFoundException) { continue; }

        foreach (var linkFile in linkFiles)
        {
          var key = linkFile.FullName.Substring(folder.FullName.Length);
          if (map.Contains(key))
            map.Remove(key);

          map.Add(key, linkFile);
        }
      }

      var assembliesList = new List<string>();
      foreach (var entry in map.Values.Cast<FileInfo>())
      {
        var extension = entry.Extension.ToLower();
        if (extension == ".gha")         assembliesList.Add(entry.FullName);
        else if (extension == ".ghlink") EnumGHLink(entry.FullName, assembliesList);
      }

      return assembliesList;
    }

    bool LoadStartupAssemblies()
    {
      // Load This Assembly as a GHA in Grasshopper
      {
        var bCoff = Instances.Settings.GetValue("Assemblies:COFF", false);
        try
        {
          Instances.Settings.SetValue("Assemblies:COFF", false);

          var location = Assembly.GetExecutingAssembly().Location;
          location = Path.Combine(Path.GetDirectoryName(location), Path.GetFileNameWithoutExtension(location) + ".GH.gha");
          if (!LoadGHA(location))
          {
            if (!File.Exists(location))
              throw new FileNotFoundException("File Not Found.", location);

            if (CentralSettings.IsLoadProtected(location))
              throw new InvalidOperationException($"Assembly '{location}' is load protected.");

            return false;
          }
        }
        finally
        {
          Instances.Settings.SetValue("Assemblies:COFF", bCoff);
        }
      }

      var assemblyList = GetAssembliesList();
      foreach (var assemblyFile in assemblyList)
      {
        bool loaded = false;
        string mainContent = string.Empty;
        string expandedContent = string.Empty;

        try
        {
          loaded = LoadGHA(assemblyFile);
        }
        catch (Exception e)
        {
          mainContent = e.Message;
          expandedContent = e.Source;
        }

        if (!loaded)
        {
          using
          (
            var taskDialog = new TaskDialog(MethodBase.GetCurrentMethod().DeclaringType.FullName)
            {
              Title = "Grasshopper Assembly Failure",
              MainIcon = External.UI.TaskDialogIcons.IconError,
              TitleAutoPrefix = false,
              AllowCancellation = false,
              MainInstruction = $"Grasshopper cannot load the external assembly {Path.GetFileName(assemblyFile)}. Please contact the provider for assistance.",
              MainContent = mainContent,
              ExpandedContent = expandedContent,
              FooterText = assemblyFile
            }
          )
          {
            taskDialog.Show();
          }
        }
      }

      GH_ComponentServer.UpdateRibbonUI();
      return true;
    }
    #endregion

    #region Revit Document
    static UnitSystem revitUnitSystem = UnitSystem.Unset;
    static UnitSystem modelUnitSystem = UnitSystem.Unset;
    public static UnitSystem ModelUnitSystem
    {
      get => Instances.ActiveCanvas is null ? UnitSystem.Unset : modelUnitSystem;
      private set => modelUnitSystem = value;
    }

    void DocumentEditor_Activated(object sender, EventArgs e)
    {
      var revitUS = UnitSystem.Unset;

      if (Revit.ActiveUIDocument?.Document is DB.Document revitDoc)
      {
        var units = revitDoc.GetUnits();
        var lengthFormatoptions = units.GetFormatOptions(DB.UnitType.UT_Length);
        revitUS = lengthFormatoptions.DisplayUnits.ToUnitSystem();
      }

      if (RhinoDoc.ActiveDoc is RhinoDoc doc)
      {
        var hasUnits = doc.ModelUnitSystem != UnitSystem.Unset && doc.ModelUnitSystem != UnitSystem.None;
        if (revitUnitSystem != revitUS || !hasUnits)
        {
          revitUnitSystem = revitUS;
          Rhinoceros.AuditUnits(doc);
        }
      }
    }

    void ActiveCanvas_DocumentChanged(GH_Canvas sender, GH_CanvasDocumentChangedEventArgs e)
    {
      if (e.OldDocument is object)
      {
        e.OldDocument.SolutionEnd -= ActiveDefinition_SolutionEnd;
        e.OldDocument.SolutionStart -= ActiveDefinition_SolutionStart;
      }

      if (e.NewDocument is object)
      {
        e.NewDocument.SolutionStart += ActiveDefinition_SolutionStart;
        e.NewDocument.SolutionEnd += ActiveDefinition_SolutionEnd;
      }
    }

    void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
    {
      var document = e.GetDocument();
      var added    = e.GetAddedElementIds();
      var deleted  = e.GetDeletedElementIds();
      var modified = e.GetModifiedElementIds();

      if (added.Count > 0 || deleted.Count > 0 || modified.Count > 0)
      {
        foreach (GH_Document definition in Instances.DocumentServer)
        {
          var change = new DocumentChangedEvent()
          {
            Operation = e.Operation,
            Document = document,
            Definition = definition
          };

          foreach (var obj in definition.Objects)
          {
            if (obj is Kernel.IGH_ElementIdParam persistentParam)
            {
              if (persistentParam.Locked)
                continue;

              if (persistentParam.DataType == GH_ParamData.remote)
                continue;

              if (persistentParam.NeedsToBeExpired(document, added, deleted, modified))
                change.ExpiredObjects.Add(persistentParam);
            }
            else if (obj is Kernel.IGH_ElementIdComponent persistentComponent)
            {
              if (persistentComponent.Locked)
                continue;

              if (persistentComponent.NeedsToBeExpired(e))
                change.ExpiredObjects.Add(persistentComponent);
            }
          }

          if (change.ExpiredObjects.Count > 0)
            DocumentChangedEvent.Enqueue(change);
        }
      }
    }

    class DocumentChangedEvent
    {
      static readonly ExternalEvent FlushQueue = ExternalEvent.Create(new FlushQueueHandler());
      class FlushQueueHandler : External.UI.EventHandler
      {
        public override string GetName() => nameof(FlushQueue);
        protected override void Execute(UIApplication app)
        {
          while (changeQueue.Count > 0)
            changeQueue.Dequeue().NewSolution();
        }
      }

      public static readonly Queue<DocumentChangedEvent> changeQueue = new Queue<DocumentChangedEvent>();
      public static void Enqueue(DocumentChangedEvent value)
      {
        changeQueue.Enqueue(value);

        if (value.Definition.SolutionState != GH_ProcessStep.Process)
        {
          // Change NOT made by Grasshopper.
          FlushQueue.Raise();
        }
      }

      public UndoOperation Operation;
      public DB.Document Document;
      public GH_Document Definition;
      public readonly List<IGH_ActiveObject> ExpiredObjects = new List<IGH_ActiveObject>();

      void NewSolution()
      {
        Debug.Assert(ExpiredObjects.Count > 0, "An empty change is been enqueued.");

        foreach (var obj in ExpiredObjects)
          obj.ExpireSolution(false);

        if (Operation == UndoOperation.TransactionCommitted)
          Definition.NewSolution(false);
      }
    }
    #endregion

    #region Transaction Groups
    readonly Stack<DB.TransactionGroup> ActiveTransactionGroups = new Stack<DB.TransactionGroup>();
    readonly Stack<GH_Document> ActiveDocuments = new Stack<GH_Document>();

    internal void StartTransactionGroups()
    {
      var now = DateTime.Now.ToString(System.Globalization.CultureInfo.CurrentUICulture);
      var name = ActiveDocuments.Peek().DisplayName;

      using (var documents = Revit.ActiveDBApplication.Documents)
      {
        foreach (var doc in documents.Cast<DB.Document>())
        {
          // Linked document do not allow transactions.
          if (doc.IsLinked)
            continue;

          // Document can not be modified during transaction recovering.
          if (doc.IsReadOnly)
            continue;

          // This document has already a transaction in course.
          if (doc.IsModifiable)
            continue;

          var group = new DB.TransactionGroup(doc, $"Grasshopper {now}: {name.TripleDot(16)}")
          {
            IsFailureHandlingForcedModal = true
          };
          group.Start();

          ActiveTransactionGroups.Push(group);
        }
      }
    }

    internal void CommitTransactionGroups()
    {
      while (ActiveTransactionGroups.Count > 0)
      {
        try
        {
          using (var group = ActiveTransactionGroups.Pop())
          {
            if (group.IsValidObject)
              group.Assimilate();
          }
        }
        catch { }
      }
    }
    #endregion

    void ActiveDefinition_SolutionStart(object sender, GH_SolutionEventArgs e)
    {
      ActiveDocuments.Push(e.Document);
      StartTransactionGroups();
    }

    void ActiveDefinition_SolutionEnd(object sender, GH_SolutionEventArgs e)
    {
      CommitTransactionGroups();
      ActiveDocuments.Pop();

      // Expire objects that contain elements modified by Grasshopper.
      {
        var expiredObjectsCount = 0;
        while (DocumentChangedEvent.changeQueue.Count > 0)
        {
          var change = DocumentChangedEvent.changeQueue.Dequeue();
          expiredObjectsCount += change.ExpiredObjects.Count;

          foreach (var obj in change.ExpiredObjects)
          {
            obj.ClearData();
            obj.AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, $"This object expired because it contained obsolete Revit elements.");
          }
        }

        if (expiredObjectsCount > 0)
        {
          Instances.DocumentEditor.SetStatusBarEvent
          (
            new GH_RuntimeMessage
            (
              expiredObjectsCount == 1 ?
              $"An object expired because it contained obsolete Revit elements." :
              $"{expiredObjectsCount} objects expired because them contained obsolete Revit elements.",
              GH_RuntimeMessageLevel.Remark,
              "Document"
            )
          );
        }
      }

      ModelUnitSystem = RhinoDoc.ActiveDoc.ModelUnitSystem;
    }
  }
}
