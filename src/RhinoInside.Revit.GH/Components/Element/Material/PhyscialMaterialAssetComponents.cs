using System;
using System.Collections.Generic;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;

using RhinoInside.Revit.External.DB.Extensions;

using DB = Autodesk.Revit.DB;

namespace RhinoInside.Revit.GH.Components.Material
{
  public abstract class BasePhysicalAssetComponent<T>
    : TransactionalChainComponent where T : PhysicalMaterialData, new()
  {
    protected AssetGHComponentAttribute ComponentInfo
    {
      get
      {
        if (_compInfo is null)
        {
          _compInfo = _assetData.GetGHComponentInfo();
          if (_compInfo is null)
            throw new Exception("Data type does not have component info");
        }
        return _compInfo;
      }
    }

    public BasePhysicalAssetComponent() : base("", "", "", "Revit", "Material") { }

    private readonly T _assetData = new T();
    private AssetGHComponentAttribute _compInfo;

    protected ParamDefinition[] GetAssetDataAsInputs(bool skipUnchangable = false)
    {
      List<ParamDefinition> inputs = new List<ParamDefinition>();

      foreach (var assetPropInfo in _assetData.GetAssetProperties())
      {
        var paramInfo = _assetData.GetGHParameterInfo(assetPropInfo);
        if (paramInfo is null)
          continue;

        if (skipUnchangable && !paramInfo.Modifiable)
          continue;

        var param = (IGH_Param) Activator.CreateInstance(paramInfo.ParamType);
        param.Name = paramInfo.Name;
        param.NickName = paramInfo.NickName;
        param.Description = paramInfo.Description;
        param.Access = paramInfo.ParamAccess;
        param.Optional = paramInfo.Optional;

        inputs.Add(ParamDefinition.FromParam(param));
      }

      return inputs.ToArray();
    }

    protected ParamDefinition[] GetAssetDataAsOutputs()
    {
      List<ParamDefinition> outputs = new List<ParamDefinition>();

      foreach (var assetPropInfo in _assetData.GetAssetProperties())
      {
        var paramInfo = _assetData.GetGHParameterInfo(assetPropInfo);
        if (paramInfo is null)
          continue;

        var param = (IGH_Param) Activator.CreateInstance(paramInfo.ParamType);
        param.Name = paramInfo.Name;
        param.NickName = paramInfo.NickName;
        param.Description = paramInfo.Description;
        param.Access = paramInfo.ParamAccess;

        outputs.Add(ParamDefinition.FromParam(param));
      }

      return outputs.ToArray();
    }

    protected static DB.PropertySetElement FindPropertySetElement(DB.Document doc, string name)
    {
      using (var collector = new DB.FilteredElementCollector(doc).
             OfClass(typeof(DB.PropertySetElement)).
             WhereParameterEqualsTo(DB.BuiltInParameter.PROPERTY_SET_NAME, name))
      {
        return collector.FirstElement() as DB.PropertySetElement;
      }
    }

    // determines matching assets based on any builtin properties
    // that are marked exclusive
    protected bool MatchesPhysicalAssetType(DB.PropertySetElement psetElement)
    {
      foreach (var assetPropInfo in _assetData.GetAssetProperties())
        foreach (var builtInPropInfo in
          _assetData.GetAPIAssetBuiltInPropertyInfos(assetPropInfo))
          if (builtInPropInfo.Exclusive
                && psetElement.get_Parameter(builtInPropInfo.ParamId) is null)
            return false;
      return true;
    }

    protected T CreateAssetDataFromInputs(IGH_DataAccess DA)
    {
      // instantiate an output object
      var output = new T();

      // set its properties
      foreach (var assetPropInfo in _assetData.GetAssetProperties())
      {
        var paramInfo = _assetData.GetGHParameterInfo(assetPropInfo);
        if (paramInfo is null)
          continue;

        IGH_Goo inputGHType = default;
        var paramIdx = Params.IndexOfInputParam(paramInfo.Name);
        if (paramIdx < 0)
          continue;

        bool hasInput = DA.GetData(paramInfo.Name, ref inputGHType);
        if (hasInput)
        {
          object inputValue = inputGHType.ScriptVariable();

          var valueRange = _assetData.GetAPIAssetPropertyValueRange(assetPropInfo);
          if (valueRange != null)
            inputValue = VerifyInputValue(paramInfo.Name, inputValue, valueRange);

          assetPropInfo.SetValue(output, inputValue);
          output.Mark(assetPropInfo.Name);
        }
      }

      return output;
    }

    protected void SetOutputsFromPropertySetElement(IGH_DataAccess DA, DB.PropertySetElement psetElement)
    {
      if (psetElement is null)
        return;

      foreach (var assetPropInfo in _assetData.GetAssetProperties())
      {
        // determine which output parameter to set the value on
        var paramInfo = _assetData.GetGHParameterInfo(assetPropInfo);
        if (paramInfo is null)
          continue;

        // grab the value from the first valid builtin param and set on the ouput
        foreach (var builtInPropInfo in _assetData.GetAPIAssetBuiltInPropertyInfos(assetPropInfo))
        {
          if (psetElement.get_Parameter(builtInPropInfo.ParamId) is DB.Parameter parameter)
          {
            DA.SetData(paramInfo.Name, parameter.AsGoo());
            break;
          }
        }
      }
    }

    protected void UpdatePropertySetElementFromData(DB.PropertySetElement psetElement, T assetData)
    {
      foreach (var assetPropInfo in _assetData.GetAssetProperties())
      {
        // skip name because it is already set
        if (assetPropInfo.Name == "Name")
          continue;

        bool hasValue = assetData.IsMarked(assetPropInfo.Name);
        if (hasValue)
        {
          object inputValue = assetPropInfo.GetValue(assetData);

          foreach (var builtInPropInfo in
            _assetData.GetAPIAssetBuiltInPropertyInfos(assetPropInfo))
            psetElement.SetParameterValue(builtInPropInfo.ParamId, inputValue);
        }
      }
    }

    #region Asset Utility Methods
    public object
    VerifyInputValue(string inputName, object inputValue, APIAssetPropValueRangeAttribute valueRangeInfo)
    {
      switch (inputValue)
      {
        case double dblVal:
          // check double max
          if (dblVal < valueRangeInfo.Min)
          {
            AddRuntimeMessage(
              GH_RuntimeMessageLevel.Warning,
              $"\"{inputName}\" value is smaller than the allowed " +
              $"minimum \"{valueRangeInfo.Min}\". Minimum value is " +
              "used instead to avoid errors"
              );
            return (object) valueRangeInfo.Min;
          }
          else if (dblVal > valueRangeInfo.Max)
          {
            AddRuntimeMessage(
              GH_RuntimeMessageLevel.Warning,
              $"\"{inputName}\" value is larger than the allowed " +
              $"maximum of \"{valueRangeInfo.Max}\". Maximum value is " +
              "used instead to avoid errors"
              );
            return (object) valueRangeInfo.Max;
          }

          break;
      }

      // if no correction has been done, return the original value
      return inputValue;
    }
    #endregion
  }

  // structural asset
  public class CreateStructuralAsset : BasePhysicalAssetComponent<StructuralAssetData>
  {
    public override Guid ComponentGuid =>
      new Guid("af2678c8-2a53-4056-9399-5a06dd9ac14d");
    public override GH_Exposure Exposure => GH_Exposure.quinary;

    public CreateStructuralAsset() : base()
    {
      Name = $"Create {ComponentInfo.Name}";
      NickName = $"C-{ComponentInfo.NickName}";
      Description = $"Create a new instance of {ComponentInfo.Description}";
    }

    protected override ParamDefinition[] Inputs => GetInputs();
    protected override ParamDefinition[] Outputs => new ParamDefinition[]
    {
      ParamDefinition.Create<Parameters.StructuralAsset>(
        name: ComponentInfo.Name,
        nickname: ComponentInfo.NickName,
        description: ComponentInfo.Description,
        access: GH_ParamAccess.item
        ),
    };

    private ParamDefinition[] GetInputs()
    {
      // build a list of inputs based on the shader data type
      // add optional document parameter as first
      var inputs = new List<ParamDefinition>()
      {
        new ParamDefinition(
            new Parameters.Document()
            {
              Name = "Document",
              NickName = "DOC",
              Description = "Document",
              Access = GH_ParamAccess.item,
              Optional = true
            },
            ParamVisibility.Voluntary
          )
      };
      inputs.AddRange(GetAssetDataAsInputs());
      return inputs.ToArray();
    }

    protected override void TrySolveInstance(IGH_DataAccess DA)
    {
      if (!Parameters.Document.GetDataOrDefault(this, DA, "Document", out var doc))
        return;

      // get required input (name, class)
      string name = default;
      if (!DA.GetData("Name", ref name))
        return;

      DB.StructuralAssetClass assetClass = default;
      if (!DA.GetData("Type", ref assetClass))
        return;

      // check naming conflicts with other asset types
      var psetElement = FindPropertySetElement(doc, name);
      if (psetElement != null && !MatchesPhysicalAssetType(psetElement))
      {
        AddRuntimeMessage(
          GH_RuntimeMessageLevel.Error,
          $"Thermal asset with same name exists already. Use a different name for this asset"
        );
        return;
      }

      StartTransaction(doc);

      // TODO: reuse psetElement if suitable
      // delete existing matching psetelement
      if (psetElement != null)
        doc.Delete(psetElement.Id);

      // create asset from input data
      var structAsset = new DB.StructuralAsset(name, assetClass);

      // we need to apply the behaviour here manually
      // otherwise the resultant DB.PropertySetElement will be missing parameters
      DB.StructuralBehavior behaviour = default;
      if (DA.GetData("Behaviour", ref behaviour))
        structAsset.Behavior = behaviour;
      else
        structAsset.Behavior = DB.StructuralBehavior.Isotropic;

      // set the asset on psetelement
      psetElement = DB.PropertySetElement.Create(doc, structAsset);

      // grab asset data from inputs
      var assetData = CreateAssetDataFromInputs(DA);
      UpdatePropertySetElementFromData(psetElement, assetData);

      // send the new asset to output
      DA.SetData(ComponentInfo.Name, new Types.StructuralAssetElement(psetElement));
    }
  }

  public class ModifyStructuralAsset : BasePhysicalAssetComponent<StructuralAssetData>
  {
    public override Guid ComponentGuid =>
      new Guid("67a74d31-0878-4b48-8efb-f4ca97389f74");

    public override GH_Exposure Exposure => GH_Exposure.quinary;

    public ModifyStructuralAsset() : base()
    {
      Name = $"Modify {ComponentInfo.Name}";
      NickName = $"M-{ComponentInfo.NickName}";
      Description = $"Modify an existing instance of {ComponentInfo.Description}";
    }

    protected override ParamDefinition[] Inputs => GetInputs();
    protected override ParamDefinition[] Outputs => new ParamDefinition[]
    {
      ParamDefinition.Create<Parameters.StructuralAsset>(
        name: ComponentInfo.Name,
        nickname: ComponentInfo.NickName,
        description: ComponentInfo.Description,
        access: GH_ParamAccess.item
        ),
    };

    private ParamDefinition[] GetInputs()
    {
      var inputs = new List<ParamDefinition>()
        {
          ParamDefinition.Create<Parameters.StructuralAsset>(
            name: ComponentInfo.Name,
            nickname: ComponentInfo.NickName,
            description: ComponentInfo.Description,
            access: GH_ParamAccess.item
          ),
        };

      foreach (ParamDefinition param in GetAssetDataAsInputs(skipUnchangable: true))
        if (param.Param.Name != "Name" && param.Param.Name != "Type")
          inputs.Add(param);

      return inputs.ToArray();
    }

    protected override void TrySolveInstance(IGH_DataAccess DA)
    {
      // get input structural asset
      var structuralAsset = default(Types.StructuralAssetElement);
      if (!DA.GetData(ComponentInfo.Name, ref structuralAsset) || structuralAsset.Value is null)
        return;

      StartTransaction(structuralAsset.Document);

      // grab asset data from inputs
      var assetData = CreateAssetDataFromInputs(DA);
      UpdatePropertySetElementFromData(structuralAsset.Value, assetData);

      // send the modified asset to output
      DA.SetData(ComponentInfo.Name, structuralAsset);
    }
  }

  public class AnalyzeStructuralAsset : BasePhysicalAssetComponent<StructuralAssetData>
  {
    public override Guid ComponentGuid =>
      new Guid("ec93f8e0-d2af-4a44-a040-89a7c40b9fc7");

    public override GH_Exposure Exposure => GH_Exposure.quinary;

    public AnalyzeStructuralAsset() : base()
    {
      Name = $"Analyze {ComponentInfo.Name}";
      NickName = $"A-{ComponentInfo.NickName}";
      Description = $"Analyzes given instance of {ComponentInfo.Description}";

    }

    protected override ParamDefinition[] Inputs => new ParamDefinition[]
    {
      ParamDefinition.Create<Parameters.StructuralAsset>(
        name: ComponentInfo.Name,
        nickname: ComponentInfo.NickName,
        description: ComponentInfo.Description,
        access: GH_ParamAccess.item
        ),
    };
    protected override ParamDefinition[] Outputs => GetAssetDataAsOutputs();

    protected override void TrySolveInstance(IGH_DataAccess DA)
    {
      var structuralAsset = default(Types.StructuralAssetElement);
      if (!DA.GetData(ComponentInfo.Name, ref structuralAsset) || structuralAsset.Value is null)
        return;

      SetOutputsFromPropertySetElement(DA, structuralAsset.Value);
    }
  }

  // thermal asset
  public class CreateThermalAsset : BasePhysicalAssetComponent<ThermalAssetData>
  {
    public override Guid ComponentGuid =>
      new Guid("bd9164c4-effb-4145-bb96-006daeaeb99a");
    public override GH_Exposure Exposure => GH_Exposure.quinary;

    public CreateThermalAsset() : base()
    {
      Name = $"Create {ComponentInfo.Name}";
      NickName = $"C-{ComponentInfo.NickName}";
      Description = $"Create a new instance of {ComponentInfo.Description}";
    }

    protected override ParamDefinition[] Inputs => GetInputs();
    protected override ParamDefinition[] Outputs => new ParamDefinition[]
    {
      ParamDefinition.Create<Parameters.ThermalAsset>(
        name: ComponentInfo.Name,
        nickname: ComponentInfo.NickName,
        description: ComponentInfo.Description,
        access: GH_ParamAccess.item
        ),
    };

    private ParamDefinition[] GetInputs()
    {
      // build a list of inputs based on the shader data type
      // add optional document parameter as first
      var inputs = new List<ParamDefinition>()
      {
        new ParamDefinition(
            new Parameters.Document()
            {
              Name = "Document",
              NickName = "DOC",
              Description = "Document",
              Access = GH_ParamAccess.item,
              Optional = true
            },
            ParamVisibility.Voluntary
          )
      };
      inputs.AddRange(GetAssetDataAsInputs());
      return inputs.ToArray();
    }

    protected override void TrySolveInstance(IGH_DataAccess DA)
    {
      if (!Parameters.Document.GetDataOrDefault(this, DA, "Document", out var doc))
        return;

      // get required input (name, class)
      string name = default;
      if (!DA.GetData("Name", ref name))
        return;

      DB.ThermalMaterialType materialType = default;
      if (!DA.GetData("Type", ref materialType))
        return;

      // check naming conflicts with other asset types
      DB.PropertySetElement psetElement = FindPropertySetElement(doc, name);
      if (psetElement != null && !MatchesPhysicalAssetType(psetElement))
      {
        AddRuntimeMessage(
          GH_RuntimeMessageLevel.Error,
          $"Thermal asset with same name exists already. Use a different name for this asset"
        );
        return;
      }

      StartTransaction(doc);

      // TODO: reuse psetElement if suitable
      // delete existing matching psetelement
      if (psetElement != null)
        doc.Delete(psetElement.Id);

      // create asset from input data
      var thermalAsset = new DB.ThermalAsset(name, materialType);

      // we need to apply the behaviour here manually
      // otherwise the resultant DB.PropertySetElement will be missing parameters
      var behaviour = DB.StructuralBehavior.Isotropic;
      DA.GetData("Behaviour", ref behaviour);

      // set the asset on psetelement
      psetElement = DB.PropertySetElement.Create(doc, thermalAsset);

      // grab asset data from inputs
      var assetData = CreateAssetDataFromInputs(DA);
      UpdatePropertySetElementFromData(psetElement, assetData);

      // send the new asset to output
      DA.SetData(ComponentInfo.Name, new Types.ThermalAssetElement(psetElement));
    }
  }

  public class ModifyThermalAsset : BasePhysicalAssetComponent<ThermalAssetData>
  {
    public override Guid ComponentGuid =>
      new Guid("2c8f541a-f831-41e1-9e19-3c5a9b07aed4");

    public override GH_Exposure Exposure => GH_Exposure.quinary;

    public ModifyThermalAsset() : base()
    {
      Name = $"Modify {ComponentInfo.Name}";
      NickName = $"M-{ComponentInfo.NickName}";
      Description = $"Modify an existing instance of {ComponentInfo.Description}";
    }

    protected override ParamDefinition[] Inputs => GetInputs();
    protected override ParamDefinition[] Outputs => new ParamDefinition[]
    {
      ParamDefinition.Create<Parameters.ThermalAsset>(
        name: ComponentInfo.Name,
        nickname: ComponentInfo.NickName,
        description: ComponentInfo.Description,
        access: GH_ParamAccess.item
        ),
    };

    private ParamDefinition[] GetInputs()
    {
      var inputs = new List<ParamDefinition>()
        {
          ParamDefinition.Create<Parameters.ThermalAsset>(
            name: ComponentInfo.Name,
            nickname: ComponentInfo.NickName,
            description: ComponentInfo.Description,
            access: GH_ParamAccess.item
          ),
        };

      foreach (ParamDefinition param in GetAssetDataAsInputs(skipUnchangable: true))
        if (param.Param.Name != "Name" && param.Param.Name != "Type")
          inputs.Add(param);

      return inputs.ToArray();
    }

    protected override void TrySolveInstance(IGH_DataAccess DA)
    {
      // get input structural asset
      var thermalAsset = default(Types.ThermalAssetElement);
      if (!DA.GetData(ComponentInfo.Name, ref thermalAsset) || thermalAsset.Value is null)
        return;

      StartTransaction(thermalAsset.Document);

      // grab asset data from inputs
      var assetData = CreateAssetDataFromInputs(DA);
      UpdatePropertySetElementFromData(thermalAsset.Value, assetData);

      // send the modified asset to output
      DA.SetData(ComponentInfo.Name, thermalAsset);
    }
  }

  public class AnalyzeThermalAsset : BasePhysicalAssetComponent<ThermalAssetData>
  {
    public override Guid ComponentGuid =>
      new Guid("c3be363d-c01d-4cf3-b8d2-c345734ae66d");

    public override GH_Exposure Exposure => GH_Exposure.quinary;

    public AnalyzeThermalAsset() : base()
    {
      Name = $"Analyze {ComponentInfo.Name}";
      NickName = $"A-{ComponentInfo.NickName}";
      Description = $"Analyzes given instance of {ComponentInfo.Description}";

    }

    protected override ParamDefinition[] Inputs => new ParamDefinition[]
    {
      ParamDefinition.Create<Parameters.ThermalAsset>(
        name: ComponentInfo.Name,
        nickname: ComponentInfo.NickName,
        description: ComponentInfo.Description,
        access: GH_ParamAccess.item
        ),
    };
    protected override ParamDefinition[] Outputs => GetAssetDataAsOutputs();

    protected override void TrySolveInstance(IGH_DataAccess DA)
    {
      var thermalAsset = default(Types.ThermalAssetElement);
      if (!DA.GetData(ComponentInfo.Name, ref thermalAsset) || thermalAsset.Value is null)
        return;

      SetOutputsFromPropertySetElement(DA, thermalAsset.Value);
    }
  }
}
