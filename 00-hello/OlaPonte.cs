using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
[assembly: CommandClass(typeof(OlaPonte.Cmds))]
namespace OlaPonte;
public class Cmds
{
    [CommandMethod("OLAPONTE")]
    public void Ola() =>
        Application.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\nPonte .NET 8 carregada com sucesso.\n");
}
