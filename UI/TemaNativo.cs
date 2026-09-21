using System.Runtime.InteropServices;

namespace AtualizadorERP.UI;

/// <summary>
/// Chamadas nativas do Windows pra fechar a lacuna que WinForms puro deixa aberta num tema escuro:
/// a barra de título (sempre branca por padrão, mesmo com o resto da janela escuro) e a barra de
/// rolagem nativa de um Panel.AutoScroll ou TextBox multilinha (cinza claro, sem forma de trocar a
/// cor via propriedade gerenciada nenhuma). As duas técnicas são as mesmas que o Explorer/Notepad
/// do próprio Windows usam no tema escuro deles.
/// </summary>
internal static class TemaNativo
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int valor, int tamanho);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SetWindowTheme(IntPtr hWnd, string? pszSubAppName, string? pszSubIdList);

    private const int DwmwaUseImmersiveDarkMode = 20; // Windows 10 20H1+ / 11.
    private const int DwmwaUseImmersiveDarkModeAntigo = 19; // builds antigas do Windows 10.

    /// <summary>Pinta a barra de título nativa de escuro -- sem isso ela fica branca em cima de
    /// uma janela inteira escura por baixo, o contraste mais chamativo de todos. Chamado depois
    /// que o Handle da janela já existe (OnHandleCreated), senão DwmSetWindowAttribute não tem em
    /// que aplicar ainda. Falha silenciosa em Windows mais antigo que não suporta o atributo --
    /// não é crítico, só cosmético.</summary>
    public static void AtivarBarraDeTituloEscura(IntPtr handleJanela)
    {
        int ligado = 1;
        if (DwmSetWindowAttribute(handleJanela, DwmwaUseImmersiveDarkMode, ref ligado, sizeof(int)) != 0)
            DwmSetWindowAttribute(handleJanela, DwmwaUseImmersiveDarkModeAntigo, ref ligado, sizeof(int));
    }

    /// <summary>Troca a barra de rolagem nativa de um controle (Panel.AutoScroll, TextBox
    /// multilinha) pra variante escura -- o mesmo truque usado pelas próprias janelas do
    /// Explorer no tema escuro do Windows. Sem chamar isto, a barra continua cinza claro mesmo
    /// com todo o resto da tela escuro.</summary>
    public static void EscurecerBarraDeRolagem(IntPtr handleControle) =>
        SetWindowTheme(handleControle, "DarkMode_Explorer", null);
}
