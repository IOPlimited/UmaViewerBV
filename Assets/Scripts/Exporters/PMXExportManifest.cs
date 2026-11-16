using System.IO;
using System.Linq;
using LibMMD.Model;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>Portable authored material inputs that PMX's fixed material record cannot express.</summary>
internal static class PMXExportManifest
{
    internal static void Write(string modelPath, RawMMDModel model)
    {
        var materials=new JArray(model.Parts.Select((part,index)=>{
            var description=JObject.Parse(part.Material.MetaInfo);
            description["index"]=index;description["name"]=part.Material.Name;
            description["doubleSided"]=part.Material.DrawDoubleFace;
            return (JToken)description;
        }));
        var document=new JObject{["schema"]="UmaViewer.ModelMaterials/1",["model"]=Path.GetFileName(modelPath),["materials"]=materials};
        File.WriteAllText(Path.ChangeExtension(modelPath,"materials.json"),document.ToString(Formatting.Indented));
        var physics = model.PhysicsExportInfo ?? new { schema = "UmaViewer.SourcePhysics/1", enabled = false };
        File.WriteAllText(Path.ChangeExtension(modelPath,"physics.json"), JsonConvert.SerializeObject(physics, Formatting.Indented));
        PMXMmeExporter.Write(modelPath,model);
    }
}
