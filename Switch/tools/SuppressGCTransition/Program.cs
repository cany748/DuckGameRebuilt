// usage: SuppressGCTransition <FNA.dll>
// Marks FNA3D's per-draw P/Invokes [SuppressGCTransition] in the published FNA.dll. Mono's native wrapper
// otherwise switches the thread into GC-safe mode around each call and copies its stack for the
// conservative scan, which costs more than many of these calls themselves. Only short calls that never
// block, wait for the GPU or call back into managed code are listed: Clear and SwapBuffers wait for
// vsync and stay as they are.
using Mono.Cecil;

string[] methods =
{
    "FNA3D_DrawIndexedPrimitives", "FNA3D_DrawPrimitives", "FNA3D_SetViewport", "FNA3D_SetScissorRect",
    "FNA3D_SetBlendState", "FNA3D_SetDepthStencilState", "FNA3D_ApplyRasterizerState", "FNA3D_VerifySampler",
    "FNA3D_ApplyVertexBufferBindings", "FNA3D_SetRenderTargets", "FNA3D_SetVertexBufferData",
    "FNA3D_SetIndexBufferData", "FNA3D_ApplyEffect", "FNA3D_BeginPassRestore", "FNA3D_EndPassRestore",
    "FNA3D_SetEffectTechnique",
};

string path = args[0];
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
using var assembly = AssemblyDefinition.ReadAssembly(new MemoryStream(File.ReadAllBytes(path)),
    new ReaderParameters { AssemblyResolver = resolver });
ModuleDefinition module = assembly.MainModule;
TypeDefinition fna3d = module.GetType("Microsoft.Xna.Framework.Graphics.FNA3D")
    ?? throw new Exception("FNA3D class not found");

AssemblyNameReference runtime = module.AssemblyReferences.First(r => r.Name == "System.Runtime");
var attributeType = new TypeReference("System.Runtime.InteropServices", "SuppressGCTransitionAttribute", module, runtime);
var constructor = new MethodReference(".ctor", module.TypeSystem.Void, attributeType) { HasThis = true };

int marked = 0;
foreach (MethodDefinition method in fna3d.Methods)
{
    if (!method.IsPInvokeImpl || Array.IndexOf(methods, method.Name) < 0)
        continue;
    if (method.CustomAttributes.Any(a => a.AttributeType.Name == "SuppressGCTransitionAttribute"))
        continue;
    method.CustomAttributes.Add(new CustomAttribute(constructor));
    marked++;
}
int expected = fna3d.Methods.Count(m => m.IsPInvokeImpl && Array.IndexOf(methods, m.Name) >= 0);
if (expected < methods.Length)
    throw new Exception($"only {expected} of the listed FNA3D methods found");
assembly.Write(path);
// The bundled Cecil can't carry portable PDBs over, and a PDB that no longer matches is of no use.
File.Delete(Path.ChangeExtension(path, ".pdb"));
Console.WriteLine($"SuppressGCTransition: {marked} FNA3D methods marked");
