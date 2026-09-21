using System.Drawing.Drawing2D;

namespace AtualizadorERP.UI;

/// <summary>
/// Paleta e helpers visuais da tela de configuração (<see cref="SetupForm"/>) -- os mesmos tokens
/// de cor do painel web (ver web/client/css/theme.css, tema escuro/"navy" padrão), traduzidos pra
/// System.Drawing.Color, pra quem abre o agente num cliente reconhecer a mesma marca do painel que
/// já usa no navegador. Fontes ficam em "Segoe UI" (não "DM Sans"/"Space Grotesk" como no painel):
/// são fontes web carregadas via Google Fonts, não instaladas por padrão num Windows de cliente --
/// embutir os arquivos .ttf no pacote só pra isto não valia a complexidade extra.
/// </summary>
internal static class Tema
{
    public static readonly Color Fundo = ColorTranslator.FromHtml("#080b11");
    public static readonly Color FundoAlt = ColorTranslator.FromHtml("#0d1219");
    public static readonly Color Superficie = ColorTranslator.FromHtml("#121a26");
    public static readonly Color SuperficieAlta = ColorTranslator.FromHtml("#1a2432");
    public static readonly Color SuperficieHover = ColorTranslator.FromHtml("#223045");
    public static readonly Color Borda = ColorTranslator.FromHtml("#24314a");
    public static readonly Color BordaForte = ColorTranslator.FromHtml("#37496b");
    public static readonly Color BordaCampo = ColorTranslator.FromHtml("#5a7095");

    public static readonly Color Texto = ColorTranslator.FromHtml("#e9eff7");
    public static readonly Color TextoSub = ColorTranslator.FromHtml("#a3b2c8");
    public static readonly Color TextoFraco = ColorTranslator.FromHtml("#8496b0");

    public static readonly Color Accent = ColorTranslator.FromHtml("#4a9eff");
    public static readonly Color AccentForte = ColorTranslator.FromHtml("#6fb4ff");
    public static readonly Color SobreAccent = ColorTranslator.FromHtml("#06101f");

    public static readonly Color Verde = ColorTranslator.FromHtml("#4ade80");
    public static readonly Color Vermelho = ColorTranslator.FromHtml("#f87171");
    public static readonly Color VermelhoForte = ColorTranslator.FromHtml("#ef5350");
    public static readonly Color Amarelo = ColorTranslator.FromHtml("#fbbf24");

    // Escala de espaçamento compartilhada entre SetupForm e Controles.cs -- ambos precisam bater
    // exatamente pro texto do cabeçalho ("Atualizador ERP") alinhar com o texto DENTRO dos cards
    // ("Dados do cliente" etc.), não só com a borda deles. Antes cada um tinha seu próprio número
    // mágico (32 aqui, 22 ali) e a diferença entre os dois é exatamente o desalinhamento visível
    // reportado numa revisão real da tela -- o texto do card ficava ~22px mais recuado que o
    // título acima dele, mesmo as duas bordas externas batendo certinho.
    public const int MargemConteudo = 32; // Panel externo (cabeçalho, área rolável, rodapé) até a borda do card.
    public const int PaddingCartao = 24;  // Borda do card até o texto/campo DENTRO dele.
    public const int MargemTexto = MargemConteudo + PaddingCartao; // Onde todo texto de nível "página" deveria começar.

    public static Font FonteBase(float tamanho = 9.5f, FontStyle estilo = FontStyle.Regular) => new("Segoe UI", tamanho, estilo);
    public static Font FonteDisplay(float tamanho = 16f, FontStyle estilo = FontStyle.Bold) => new("Segoe UI Semibold", tamanho, estilo);

    /// <summary>
    /// Caminho de retângulo arredondado -- WinForms não tem "border-radius" nativo. Usado tanto
    /// pra recortar a Region de um controle (efeito visual das quinas) quanto pra desenhar a
    /// borda por cima no Paint (a Region sozinha corta o controle mas não desenha contorno nenhum).
    /// </summary>
    public static GraphicsPath CaminhoArredondado(Rectangle area, int raio)
    {
        int d = raio * 2;
        var caminho = new GraphicsPath();
        if (d <= 0 || d > area.Width || d > area.Height)
        {
            caminho.AddRectangle(area);
            return caminho;
        }

        caminho.AddArc(area.X, area.Y, d, d, 180, 90);
        caminho.AddArc(area.Right - d, area.Y, d, d, 270, 90);
        caminho.AddArc(area.Right - d, area.Bottom - d, d, d, 0, 90);
        caminho.AddArc(area.X, area.Bottom - d, d, d, 90, 90);
        caminho.CloseFigure();
        return caminho;
    }
}
