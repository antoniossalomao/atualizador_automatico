using System.Drawing.Drawing2D;

namespace AtualizadorERP.UI;

/// <summary>Variante visual de <see cref="BotaoTema"/> -- só cor de preenchimento/borda muda;
/// o resto (quinas arredondadas, hover, texto) é igual nas três.</summary>
internal enum VarianteBotao
{
    /// <summary>Ação principal da tela (ex.: "Salvar e instalar serviço") -- fundo cheio na cor
    /// de destaque do tema.</summary>
    Primario,

    /// <summary>Ação secundária (ex.: "Cancelar") -- só contorno, fundo transparente/superfície.</summary>
    Secundario,

    /// <summary>Ação destrutiva ou de alerta -- fundo cheio vermelho. Não usado na tela de
    /// configuração hoje, mas já deixado pronto (mesmo padrão do painel web, que tem essa
    /// variante pros botões de "excluir").</summary>
    Perigo,
}

/// <summary>
/// Button com quinas arredondadas e paleta do <see cref="Tema"/> -- o WinForms padrão (Button
/// comum, mesmo com FlatStyle.Flat) não dá pra deixar com cara de botão do painel web sem
/// desenhar na mão. Feito com Region (recorte) + Paint (preenchimento/borda/texto) em vez de um
/// terceiro componente NuGet só pra isto.
/// </summary>
internal sealed class BotaoTema : Button
{
    private const int Raio = 8;
    private bool _mouseSobre;
    private bool _mousePressionado;

    public VarianteBotao Variante { get; set; } = VarianteBotao.Primario;

    public BotaoTema()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        FlatAppearance.MouseDownBackColor = Color.Transparent;
        FlatAppearance.MouseOverBackColor = Color.Transparent;
        BackColor = Color.Transparent;
        Font = Tema.FonteBase(9.5f, FontStyle.Bold);
        Cursor = Cursors.Hand;
        Height = 38;
        TabStop = true;
    }

    protected override void OnMouseEnter(EventArgs e) { _mouseSobre = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _mouseSobre = false; _mousePressionado = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs mevent) { _mousePressionado = true; Invalidate(); base.OnMouseDown(mevent); }
    protected override void OnMouseUp(MouseEventArgs mevent) { _mousePressionado = false; Invalidate(); base.OnMouseUp(mevent); }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        var area = new Rectangle(0, 0, Width, Height);
        using var caminho = Tema.CaminhoArredondado(area, Raio);
        Region = new Region(caminho);
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var g = pevent.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var area = new Rectangle(0, 0, Width - 1, Height - 1);
        using var caminho = Tema.CaminhoArredondado(area, Raio);

        var (fundo, fundoHover, borda, texto) = Variante switch
        {
            VarianteBotao.Primario => (Tema.Accent, Tema.AccentForte, Tema.Accent, Tema.SobreAccent),
            VarianteBotao.Perigo => (Tema.VermelhoForte, Tema.Vermelho, Tema.VermelhoForte, Color.White),
            _ => (Tema.Superficie, Tema.SuperficieHover, Tema.BordaCampo, Tema.Texto),
        };

        Color corBase = _mouseSobre ? fundoHover : fundo;
        Color corFundo = !Enabled ? Tema.SuperficieAlta : (_mousePressionado ? ControlPaint.Dark(corBase, 0.1f) : corBase);
        using (var brush = new SolidBrush(corFundo)) g.FillPath(brush, caminho);
        if (Variante == VarianteBotao.Secundario)
        {
            using var caneta = new Pen(borda, 1);
            g.DrawPath(caneta, caminho);
        }

        Color corTexto = Enabled ? texto : Tema.TextoFraco;
        TextRenderer.DrawText(g, Text, Font, area, corTexto, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}
