using System.Reflection;
using Xunit;

namespace Cabinet.Diagnostic.Core.Tests;

/// <summary>
/// Vérifie qu'un assemblage de l'outil ne référence aucune bibliothèque réseau.
/// Partagé par lien de fichier avec les autres projets de tests.
/// </summary>
public static class NetworkGuard
{
    private static readonly string[] Forbidden =
    {
        "System.Net.Http",
        "System.Net.Sockets",
        "System.Net.Requests",
        "System.Net.WebClient",
        "System.Net.WebSockets",
        "System.Net.Mail",
        "System.Net.NetworkInformation",
    };

    public static void AssertNoNetworkReferences(Assembly assembly)
    {
        var referenced = assembly.GetReferencedAssemblies().Select(a => a.Name ?? "").ToArray();
        foreach (var name in Forbidden)
        {
            Assert.DoesNotContain(name, referenced);
        }
    }
}
