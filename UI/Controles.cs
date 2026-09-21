using System.Drawing.Drawing2D;

namespace AtualizadorERP.UI;

/// <summary>
/// Base comum de todo controle com quina arredondada desenhada na mão (<see cref="CartaoPainel"/>,
/// <see cref="CampoTexto"/>). Resolve um problema específico de WinForms: pintar só o CAMINHO
/// arredondado no OnPaint deixa a pintura de fundo PADRÃO do Panel (um retângulo reto, feita antes
/// do OnPaint rodar) vazando pelos quatro cantos -- um quadradinho da cor errada atrás de cada
/// quina arredondada. A correção é pintar o fundo, no OnPaintBackground, com a cor do PAI (não a
/// cor do próprio controle) -- assim o que "vaza" atrás da quina é a mesma cor do que já está por
/// trás dele, e o corte arredondado por cima disfarça a emenda.
/// </summary>
internal abstract class PainelArredondado : Panel
{
    protected PainelArredondado()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
        pevent.Graphics.Clear(Parent?.BackColor ?? Tema.Fundo);
    }
}

/// <summary>Card com fundo/borda arredondados, usado pra agrupar seções na tela de configuração
/// (mesmo papel visual do ".card" do painel web).</summary>
internal sealed class CartaoPainel : PainelArredondado
{
    public CartaoPainel()
    {
        BackColor = Tema.Superficie;
        Padding = new Padding(Tema.PaddingCartao);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var area = new Rectangle(0, 0, Width - 1, Height - 1);
        using var caminho = Tema.CaminhoArredondado(area, 12);
        using var fundo = new SolidBrush(Tema.Superficie);
        g.FillPath(fundo, caminho);
        using var caneta = new Pen(Tema.Borda, 1);
        g.DrawPath(caneta, caminho);
    }
}

/// <summary>
/// Campo de texto com borda arredondada e destaque de foco na cor de acento -- o TextBox nativo do
/// WinForms não aceita cor de borda customizada nem quina arredondada, então o controle de verdade
/// (<see cref="Texto"/>) fica SEM borda própria (BorderStyle.None), embutido dentro deste painel,
/// que desenha a borda por fora.
/// </summary>
internal sealed class CampoTexto : PainelArredondado
{
    public TextBox Texto { get; }

    private bool _focado;

    public string Valor
    {
        get => Texto.Text;
        set => Texto.Text = value;
    }

    public CampoTexto(bool senha = false, bool multilinha = false)
    {
        BackColor = Tema.SuperficieAlta;
        Height = multilinha ? 78 : 36;
        Padding = new Padding(11, multilinha ? 8 : 0, 11, multilinha ? 8 : 0);

        Texto = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Tema.SuperficieAlta,
            ForeColor = Tema.Texto,
            Font = Tema.FonteBase(9.5f),
            UseSystemPasswordChar = senha,
            Multiline = multilinha,
            ScrollBars = multilinha ? ScrollBars.Vertical : ScrollBars.None,
        };

        if (multilinha)
        {
            Texto.Dock = DockStyle.Fill;
        }
        else
        {
            // Dock.Fill esticaria o TextBox de linha única pra cobrir a altura toda do campo --
            // ele não centraliza o próprio texto verticalmente sozinho, então sobra baseline
            // colada no topo. Ancorado nas laterais e posicionado manualmente no meio.
            Texto.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            Resize += (_, _) => PosicionarLinhaUnica();
        }

        Texto.GotFocus += (_, _) => { _focado = true; Invalidate(); };
        Texto.LostFocus += (_, _) => { _focado = false; Invalidate(); };

        Controls.Add(Texto);
        if (!multilinha) PosicionarLinhaUnica();
    }

    private void PosicionarLinhaUnica()
    {
        int y = Padding.Top + (Height - Padding.Top - Padding.Bottom - Texto.Height) / 2;
        Texto.SetBounds(Padding.Left, Math.Max(Padding.Top, y), Math.Max(0, Width - Padding.Left - Padding.Right), Texto.Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var area = new Rectangle(0, 0, Width - 1, Height - 1);
        using var caminho = Tema.CaminhoArredondado(area, 7);
        using var fundo = new SolidBrush(Tema.SuperficieAlta);
        g.FillPath(fundo, caminho);
        using var caneta = new Pen(_focado ? Tema.Accent : Tema.BordaCampo, _focado ? 1.6f : 1f);
        g.DrawPath(caneta, caminho);
    }
}

/// <summary>Rótulo de campo, no mesmo tom "texto-sub" do painel web (label discreto acima do
/// input). Método, não classe: um Label puro já basta, só falta aplicar a paleta/fonte certas.</summary>
internal static class Rotulos
{
    public static Label Campo(string texto) => new()
    {
        Text = texto,
        ForeColor = Tema.TextoSub,
        BackColor = Color.Transparent,
        Font = Tema.FonteBase(9f, FontStyle.Bold),
        AutoSize = true,
        Margin = new Padding(1, 0, 0, 6),
    };

    public static Label Ajuda(string texto) => new()
    {
        Text = texto,
        ForeColor = Tema.TextoFraco,
        BackColor = Color.Transparent,
        Font = Tema.FonteBase(8.25f),
        AutoSize = true,
        MaximumSize = new Size(520, 0),
        Margin = new Padding(1, 4, 0, 0),
    };
}
