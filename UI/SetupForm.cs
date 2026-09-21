using System.Text;

using AtualizadorERP.Services;

namespace AtualizadorERP.UI;

/// <summary>
/// Tela que aparece quando alguém dá duplo clique no AtualizadorERP.exe (fora do SCM -- ver
/// Program.cs) em vez do agente simplesmente recusar subir sem "atualizador.ini". Reúne os dois
/// passos manuais que o README hoje descreve em "Instalando num cliente" -- preencher o .ini e
/// registrar o serviço com "sc.exe create"/"sc.exe start" -- numa única tela, prenchida de novo a
/// cada abertura (mesmo se o .ini já existir) pra também servir de tela de reconfiguração.
///
/// Todo o layout usa FlowLayoutPanel (TopDown) + AutoSize em cascata (painel do card dentro do
/// flow externo, flow interno dentro do card) em vez de coordenadas fixas -- assim abrir/fechar o
/// card "Avançado" reflui o resto da tela sozinho, sem recalcular posição de mais nada na mão.
/// </summary>
internal sealed class SetupForm : Form
{
    private const int LarguraConteudo = 560;

    // Largura útil DENTRO de um card, já descontando o Padding de cada lado que CartaoPainel
    // sempre usa (Tema.PaddingCartao, ver Controles.cs). Passada explicitamente pra
    // AdicionarCampo em vez de lida de volta em "destino.Width" -- um FlowLayoutPanel AutoSize
    // encolhe a própria largura pra caber só no filho mais estreito JÁ ADICIONADO até aquele
    // instante (reproduzido ao vivo: depois de só o título "Dados do cliente" ~140px, os rótulos
    // de ajuda seguintes mediam contra isso e quebravam linha cedo demais) -- uma constante fixa
    // não sofre esse efeito cascata.
    private const int LarguraCampo = LarguraConteudo - (2 * Tema.PaddingCartao);

    // Lista default de scripts legados quebrados do próprio B_Vendas (ver ScriptRunnerService e
    // atualizador.ini.example) -- duplicada aqui de propósito: o .example é o que uma instalação
    // manual/antiga usa como ponto de partida, esta constante é o que a tela de configuração
    // sugere pra quem nunca teve um atualizador.ini antes. Se um script legado novo for
    // confirmado quebrado no futuro, adicione nos dois lugares.
    // ", " (vírgula + espaço), não só vírgula: ConfiguracaoAgente.LerIni/ListaOpcional faz
    // TrimEntries no split, então o espaço não muda o valor -- só existe pra quebra de linha do
    // TextBox (ver CampoTexto) cair num espaço em vez de no meio de um token, como acontecia com
    // a lista real do cliente (sem espaço nenhum) quebrando tipo "B_Ven / das.exe".
    private const string ScriptsIgnoradosPadrao =
        "20241009Altera_Procedure_Inventario_NFCe.sql, 20070409Cria_campo_icms_tab_empresa.sql, 20070409Cria_campo_preco_dolar_tab_empresa.sql, " +
        "20070409Cria_campo_utiliza_grade_produto_tab_empresa.sql, 20071220Cria_campos_atualiza_saldos_tabela_empresa.sql, 20080102Cria_campos_atualiza_validade_lote_tabela_empresa.sql, " +
        "20110114Cria_campo_CAS_DEC_QTDE_Tabela_Empresa.sql, 20110114Cria_campo_CAS_DEC_VALOR_Tabela_Empresa.sql, 20110114Cria_campo_EMP_UTIL_PRECO_CUSTO_TAB_PROD_Tabela_Empresa.sql, " +
        "20110114Valor_campo_CAS_DEC_QTDE_Tabela_Empresa.sql, 20110114Valor_campo_EMP_CAS_DEC_VALOR_Tabela_Empresa.sql, 20110114Valor_campo_EMP_UTIL_PRECO_CUSTO_TAB_PROD_Tabela_Empresa.sql, " +
        "20200204Cria_campo_CODTABPRECO_em_FSPEDIDOITENS.sql, 20230116Altera_tabela_PPP_EXCLUIDO_adiciona_motivo.sql, 20080129Deleta_Relaciona_tabela_processo.sql, " +
        "20080130Deleta_campo_tabela_receita_processo.sql, 20170508Aparaga_trigger_CHECKOUT_NATOP_BIU100.sql, 20200813Altera_Campos_Tabela_Nutricional.sql, " +
        "20220225Cria_Trigger_PRE_PEDIDO_ITENS_CANCELADOS_AU0.sql, 20260416Altera_Trigger_ITEM_PROD_INS_RECEITA_REQUISICA.sql";

    private const string SugestaoSistemas = "B_Vendas:B_Vendas.exe, B_NFe:B_NFE.exe, B_Importa:BImportaXML.exe, B_Ordem:B_Ordem_Servico.exe";

    private readonly string _caminhoIni = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "atualizador.ini");

    private readonly CampoTexto _campoCodigoCliente = new();
    private readonly CampoTexto _campoSistemas = new(multilinha: true);
    private readonly CampoTexto _campoSistemasComScript = new();
    private readonly CampoTexto _campoApiToken = new(senha: true);
    private readonly CampoTexto _campoDbPassword = new(senha: true);

    private readonly CampoTexto _campoApiUrl = new();
    private readonly CampoTexto _campoDbUser = new();
    private readonly CampoTexto _campoDbPort = new();
    private readonly CampoTexto _campoGfixPath = new();
    private readonly CampoTexto _campoGbakPath = new();
    private readonly CampoTexto _campoIsqlPath = new();
    private readonly CampoTexto _campoJuniorFdb = new();
    private readonly CampoTexto _campoBexeFdb = new();
    private readonly CampoTexto _campoPastaTrabalho = new();
    private readonly CampoTexto _campoPastaBackups = new();
    private readonly CampoTexto _campoBackupsParaManter = new();
    private readonly CampoTexto _campoScriptsIgnorados = new(multilinha: true);

    private readonly Label _rotuloStatus;
    private readonly BotaoTema _botaoSalvar;
    private readonly BotaoTema _botaoCancelar;
    private readonly CartaoPainel _cartaoAvancado;
    private readonly Label _alternadorAvancado;
    private bool _avancadoVisivel;

    // Guardados pra ReposicionarConteudo poder recentralizar tudo -- ver comentário lá.
    private FlowLayoutPanel _conteudo = null!;
    private Label _tituloHeader = null!;
    private Label _subtituloHeader = null!;
    private Panel _rodape = null!;

    public SetupForm()
    {
        Text = "Atualizador ERP";
        ClientSize = new Size(LarguraConteudo + 64, 680);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Tema.Fundo;
        Font = Tema.FonteBase();

        var cabecalho = ConstruirCabecalho();

        // Padding só vertical aqui -- o horizontal é decidido em ReposicionarConteudo (Left do
        // "conteudo" abaixo), não por Padding fixo, porque Padding sozinho deixa o conteúdo
        // GRUDADO à esquerda se a janela renderizar mais larga que LarguraConteudo (visto ao vivo:
        // acontece com escala de DPI diferente de 100%) em vez de continuar centralizado.
        var areaRolavel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Tema.Fundo,
            Padding = new Padding(0, 12, 0, 28),
        };

        _conteudo = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Tema.Fundo,
            Width = LarguraConteudo,
            Top = 12, // Explícito, não só via Padding do pai -- ver ReposicionarConteudo pro Left.
        };
        var conteudo = _conteudo;

        conteudo.Controls.Add(ConstruirCartaoDadosCliente());
        conteudo.Controls.Add(Espacador(14));
        conteudo.Controls.Add(ConstruirCartaoCredenciais());
        conteudo.Controls.Add(Espacador(10));

        _alternadorAvancado = new Label
        {
            Text = "▸  Mostrar opções avançadas",
            ForeColor = Tema.Accent,
            BackColor = Color.Transparent,
            Font = Tema.FonteBase(9f, FontStyle.Bold),
            AutoSize = true,
            Cursor = Cursors.Hand,
            // "Tema.PaddingCartao", não um número solto: alinha esse texto com o texto DENTRO dos
            // cards acima (mesma régua de Tema.MargemTexto), já que ele não mora dentro de um
            // card próprio pra herdar o padding automaticamente.
            Margin = new Padding(Tema.PaddingCartao, 4, 0, 10),
        };
        _alternadorAvancado.Click += (_, _) => AlternarAvancado();
        conteudo.Controls.Add(_alternadorAvancado);

        _cartaoAvancado = ConstruirCartaoAvancado();
        _cartaoAvancado.Visible = false;
        conteudo.Controls.Add(_cartaoAvancado);

        areaRolavel.Controls.Add(conteudo);

        _rodape = new Panel { Dock = DockStyle.Bottom, Height = 84, BackColor = Tema.FundoAlt };
        var rodape = _rodape;
        using (var separador = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Tema.Borda }) rodape.Controls.Add(separador);

        _rotuloStatus = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 26,
            Padding = new Padding(0, 12, 0, 0),
            ForeColor = Tema.TextoSub,
            BackColor = Color.Transparent,
            Font = Tema.FonteBase(8.5f),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        rodape.Controls.Add(_rotuloStatus);

        var painelBotoes = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 46,
            BackColor = Color.Transparent,
        };
        _botaoSalvar = new BotaoTema { Text = "Salvar e instalar serviço", Width = 220, Variante = VarianteBotao.Primario };
        _botaoSalvar.Click += BotaoSalvar_Click;
        _botaoCancelar = new BotaoTema { Text = "Cancelar", Width = 110, Variante = VarianteBotao.Secundario, Margin = new Padding(0, 0, 12, 0) };
        _botaoCancelar.Click += (_, _) => Close();
        painelBotoes.Controls.Add(_botaoSalvar);
        painelBotoes.Controls.Add(_botaoCancelar);
        rodape.Controls.Add(painelBotoes);

        Controls.Add(areaRolavel);
        Controls.Add(rodape);
        Controls.Add(cabecalho);

        Resize += (_, _) => ReposicionarConteudo();
        ReposicionarConteudo();

        // "Handle" força a criação do HWND nativo na hora (normalmente só aconteceria quando a
        // janela fosse mostrada) -- precisa disso pra SetWindowTheme ter em que aplicar já aqui
        // no construtor, em vez de esperar um evento Load separado só pra isto.
        TemaNativo.EscurecerBarraDeRolagem(areaRolavel.Handle);
        TemaNativo.EscurecerBarraDeRolagem(_campoSistemas.Texto.Handle);
        TemaNativo.EscurecerBarraDeRolagem(_campoScriptsIgnorados.Texto.Handle);

        try
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch (Exception)
        {
            // Sem ícone embutido no .exe (ex.: rodando direto do bin\Debug fora de uma publicação
            // com ApplicationIcon aplicado) -- fica no ícone genérico padrão do WinForms, não é
            // motivo pra travar a tela inteira.
        }

        PreencherComIniExistente();
    }

    // DwmSetWindowAttribute só tem efeito depois que o HWND da janela existe de verdade -- não dá
    // pra chamar isso no construtor (a janela ainda não foi criada nesse ponto), precisa deste
    // override, que roda exatamente no momento em que o Handle passa a existir.
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        TemaNativo.AtivarBarraDeTituloEscura(Handle);
    }

    private Panel ConstruirCabecalho()
    {
        var cabecalho = new Panel { Dock = DockStyle.Top, Height = 80, BackColor = Tema.FundoAlt };
        // "Location.X" definido de verdade em ReposicionarConteudo, não aqui -- só um valor
        // inicial qualquer até a primeira chamada (que já acontece antes da janela aparecer).
        _tituloHeader = new Label
        {
            Text = "Atualizador ERP",
            ForeColor = Tema.Texto,
            BackColor = Color.Transparent,
            Font = Tema.FonteDisplay(17f),
            AutoSize = true,
            Location = new Point(Tema.MargemTexto, 14),
        };
        _subtituloHeader = new Label
        {
            Text = File.Exists(_caminhoIni) ? "Reconfigurar o agente instalado nesta pasta" : "Configuração inicial do agente",
            ForeColor = Tema.TextoSub,
            BackColor = Color.Transparent,
            Font = Tema.FonteBase(9f),
            AutoSize = true,
            Location = new Point(Tema.MargemTexto, 46),
        };
        cabecalho.Controls.Add(_tituloHeader);
        cabecalho.Controls.Add(_subtituloHeader);
        using var separador = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Tema.Borda };
        cabecalho.Controls.Add(separador);
        return cabecalho;
    }

    /// <summary>
    /// Centraliza a coluna de conteúdo (cards, cabeçalho, rodapé) na largura REAL da janela em vez
    /// de confiar num Padding fixo -- reproduzido ao vivo: com a janela renderizando mais larga que
    /// <see cref="LarguraConteudo"/> (escala de DPI diferente de 100% no monitor, ou a pessoa
    /// arrastando a borda), um Padding fixo deixava tudo grudado no canto esquerdo com uma sobra
    /// de espaço vazio à direita, em vez de continuar centralizado. Chamado uma vez no fim do
    /// construtor e de novo a cada Resize.
    /// </summary>
    private void ReposicionarConteudo()
    {
        int margem = Math.Max(Tema.MargemConteudo, (ClientSize.Width - LarguraConteudo) / 2);
        int margemTexto = margem + Tema.PaddingCartao;

        _conteudo.Left = margem;
        _tituloHeader.Left = margemTexto;
        _subtituloHeader.Left = margemTexto;
        _rodape.Padding = new Padding(margem, 0, margem, 0);
    }

    private CartaoPainel ConstruirCartaoDadosCliente()
    {
        var cartao = NovoCartao();
        var flow = NovoFlowInterno(cartao);

        flow.Controls.Add(TituloCartao("Dados do cliente"));
        AdicionarCampo(flow, "Código do cliente", "Cadastrado na aba Clientes do painel (ex.: C014884).", _campoCodigoCliente);
        AdicionarCampo(flow, "Sistemas", "Todos os sistemas que a empresa distribui, \"Nome:executavel.exe\" separados por vírgula.", _campoSistemas);
        AdicionarCampo(flow, "Sistemas com script", "Quais desses rodam script contra o JUNIOR.fdb (normalmente só B_Vendas). Deixe em branco se nenhum.", _campoSistemasComScript);

        return FecharCartao(cartao, flow);
    }

    private CartaoPainel ConstruirCartaoCredenciais()
    {
        var cartao = NovoCartao();
        var flow = NovoFlowInterno(cartao);

        // Margin bottom bate com o de TituloCartao (18) de propósito: o título aqui está
        // embrulhado numa linha horizontal (pra caber o checkbox "Mostrar" do lado), então o
        // Margin PRÓPRIO de TituloCartao não conta pro espaçamento vertical -- só o de
        // "linhaTitulo" conta. Sem repetir o mesmo valor aqui, esse card ficava com bem menos
        // respiro abaixo do título que os outros dois (6px em vez de 18px).
        var linhaTitulo = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 18) };
        linhaTitulo.Controls.Add(TituloCartao("Credenciais"));
        var mostrarSenha = new CheckBox
        {
            Text = "Mostrar",
            ForeColor = Tema.TextoSub,
            BackColor = Color.Transparent,
            Font = Tema.FonteBase(8.5f),
            AutoSize = true,
            Margin = new Padding(16, 5, 0, 0),
        };
        mostrarSenha.CheckedChanged += (_, _) =>
        {
            _campoApiToken.Texto.UseSystemPasswordChar = !mostrarSenha.Checked;
            _campoDbPassword.Texto.UseSystemPasswordChar = !mostrarSenha.Checked;
        };
        linhaTitulo.Controls.Add(mostrarSenha);
        flow.Controls.Add(linhaTitulo);

        AdicionarCampo(flow, "Token da API", "Precisa bater com AGENT_API_TOKEN do servidor (web/server/.env).", _campoApiToken);
        AdicionarCampo(flow, "Senha do Firebird", "Senha do usuário SYSDBA (ou o que estiver em DB_USER, na seção Avançado).", _campoDbPassword);

        return FecharCartao(cartao, flow);
    }

    private CartaoPainel ConstruirCartaoAvancado()
    {
        var cartao = NovoCartao();
        var flow = NovoFlowInterno(cartao);

        flow.Controls.Add(TituloCartao("Avançado"));
        var ajudaGeral = Rotulos.Ajuda("Tudo aqui já tem um valor padrão sensato (ver atualizador.ini.example) -- só preencha se a estrutura deste cliente for diferente.");
        ajudaGeral.Margin = new Padding(2, 0, 0, 12);
        flow.Controls.Add(ajudaGeral);

        AdicionarCampo(flow, "URL da API", "Default: http://localhost:3000/api", _campoApiUrl);
        AdicionarCampo(flow, "Usuário do Firebird", "Default: SYSDBA", _campoDbUser);
        AdicionarCampo(flow, "Porta do Firebird", "Default: 3050", _campoDbPort);
        AdicionarCampo(flow, "Caminho do gfix.exe", "Default: instalação padrão do Firebird 2.5 (32 bits).", _campoGfixPath);
        AdicionarCampo(flow, "Caminho do gbak.exe", "Default: instalação padrão do Firebird 2.5 (32 bits).", _campoGbakPath);
        AdicionarCampo(flow, "Caminho do isql.exe", "Default: instalação padrão do Firebird 2.5 (32 bits).", _campoIsqlPath);
        AdicionarCampo(flow, "Caminho do JUNIOR.fdb", "Default: um nível acima da pasta do agente.", _campoJuniorFdb);
        AdicionarCampo(flow, "Caminho do BEXE.fdb", "Default: um nível acima da pasta do agente.", _campoBexeFdb);
        AdicionarCampo(flow, "Pasta de trabalho", "Default: \"_trabalho\" dentro da pasta do agente.", _campoPastaTrabalho);
        AdicionarCampo(flow, "Pasta de backups", "Default: \"Backups\" dentro da pasta do agente.", _campoPastaBackups);
        AdicionarCampo(flow, "Backups a manter", "Quantos ciclos ficam guardados antes de apagar o mais antigo. Default: 10.", _campoBackupsParaManter);
        AdicionarCampo(flow, "Scripts ignorados", "Scripts legados conhecidos como quebrados -- o agente nunca tenta rodar estes.", _campoScriptsIgnorados);

        return FecharCartao(cartao, flow);
    }

    private static CartaoPainel NovoCartao() => new() { Width = LarguraConteudo, Margin = new Padding(0), AutoSize = false };

    // "Location", não "Dock.Top": um FlowLayoutPanel com AutoSize=true E Dock=Top dentro de um
    // Panel (CartaoPainel) que TAMBÉM é AutoSize não cresce direito na prática -- reproduzido ao
    // vivo (screenshot real da tela): o card cortava depois do primeiro campo, mesmo com mais
    // campos adicionados ao flow interno depois. Only com Location fixo (sem Dock) o
    // FlowLayoutPanel recalcula a própria altura a cada Controls.Add, e FecharCartao (chamado no
    // fim de cada Construir*Cartao) lê essa altura JÁ CORRETA pra dimensionar o card por fora --
    // determinístico, sem depender de uma segunda passada de layout que talvez nunca aconteça.
    private static FlowLayoutPanel NovoFlowInterno(CartaoPainel cartao)
    {
        var flow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowOnly, // ver comentário de LarguraCampo -- nunca encolhe abaixo da largura fixa.
            BackColor = Color.Transparent,
            Width = LarguraCampo,
            Location = new Point(cartao.Padding.Left, cartao.Padding.Top),
        };
        cartao.Controls.Add(flow);
        return flow;
    }

    // Chamado no fim de cada Construir*Cartao, depois do último Controls.Add no flow interno --
    // "flow.Height" já reflete o total real nesse ponto (AutoSize sem Dock recalcula a cada
    // adição, não só numa passada de layout futura).
    private static CartaoPainel FecharCartao(CartaoPainel cartao, FlowLayoutPanel flow)
    {
        cartao.Height = flow.Height + cartao.Padding.Vertical;
        return cartao;
    }

    private static Label TituloCartao(string texto) => new()
    {
        Text = texto,
        ForeColor = Tema.Texto,
        BackColor = Color.Transparent,
        Font = Tema.FonteBase(12f, FontStyle.Bold),
        AutoSize = true,
        Margin = new Padding(0, 0, 0, 18),
    };

    private static Panel Espacador(int altura) => new() { Height = altura, Width = 1, BackColor = Color.Transparent };

    // Régua de espaçamento vertical de um grupo campo (rótulo → campo → ajuda → próximo rótulo):
    // 6 / 6 / 20 -- perto o bastante entre rótulo/campo/ajuda pra ler como UM grupo, com folga
    // clara antes do PRÓXIMO grupo começar. Antes eram 4/4/14, meio apertado demais pra separar
    // visualmente um campo do outro (reportado como "desalinhado"/precisando de polida numa
    // revisão real da tela).
    private void AdicionarCampo(FlowLayoutPanel destino, string rotulo, string ajuda, CampoTexto campo)
    {
        campo.Width = LarguraCampo;
        campo.Margin = new Padding(0, 0, 0, 6);
        destino.Controls.Add(Rotulos.Campo(rotulo));
        destino.Controls.Add(campo);
        var ajudaLabel = Rotulos.Ajuda(ajuda);
        ajudaLabel.MaximumSize = new Size(LarguraCampo, 0);
        ajudaLabel.Margin = new Padding(1, 4, 0, 20);
        destino.Controls.Add(ajudaLabel);
    }

    private void AlternarAvancado()
    {
        _avancadoVisivel = !_avancadoVisivel;
        _cartaoAvancado.Visible = _avancadoVisivel;
        _alternadorAvancado.Text = _avancadoVisivel ? "▾  Ocultar opções avançadas" : "▸  Mostrar opções avançadas";
    }

    private void PreencherComIniExistente()
    {
        if (!File.Exists(_caminhoIni))
        {
            _campoSistemas.Valor = SugestaoSistemas;
            _campoScriptsIgnorados.Valor = ScriptsIgnoradosPadrao;
            return;
        }

        var valores = ConfiguracaoAgente.LerIni(_caminhoIni);
        void Preencher(CampoTexto campo, string chave)
        {
            if (valores.TryGetValue(chave, out var valor)) campo.Valor = valor;
        }

        Preencher(_campoCodigoCliente, "CODIGO_CLIENTE");
        Preencher(_campoSistemas, "SISTEMAS");
        Preencher(_campoSistemasComScript, "SISTEMAS_COM_SCRIPT");
        Preencher(_campoApiToken, "API_TOKEN");
        Preencher(_campoDbPassword, "DB_PASSWORD");
        Preencher(_campoApiUrl, "API_URL");
        Preencher(_campoDbUser, "DB_USER");
        Preencher(_campoDbPort, "DB_PORT");
        Preencher(_campoGfixPath, "GFIX_PATH");
        Preencher(_campoGbakPath, "GBAK_PATH");
        Preencher(_campoIsqlPath, "ISQL_PATH");
        Preencher(_campoJuniorFdb, "JUNIOR_FDB");
        Preencher(_campoBexeFdb, "BEXE_FDB");
        Preencher(_campoPastaTrabalho, "PASTA_TRABALHO");
        Preencher(_campoPastaBackups, "PASTA_BACKUPS");
        Preencher(_campoBackupsParaManter, "BACKUPS_PARA_MANTER");

        _campoScriptsIgnorados.Valor = valores.TryGetValue("SCRIPTS_IGNORADOS", out var scripts) && !string.IsNullOrWhiteSpace(scripts)
            ? scripts
            : ScriptsIgnoradosPadrao;
    }

    // Mesmas regras de "Obrigatorio"/"ListaDeSistemas"/porta em ConfiguracaoAgente.cs, checadas
    // aqui ANTES de gravar o .ini -- sem isso, um campo faltando ou mal formatado só aparecia como
    // falha ao subir o serviço (evento do Windows, nada visível nesta tela).
    private string? ValidarCampos()
    {
        if (string.IsNullOrWhiteSpace(_campoCodigoCliente.Valor)) return "Preencha o Código do cliente.";
        if (string.IsNullOrWhiteSpace(_campoSistemas.Valor)) return "Preencha ao menos um sistema em Sistemas.";

        foreach (var item in _campoSistemas.Valor.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            int separador = item.IndexOf(':');
            if (separador <= 0 || separador == item.Length - 1)
                return $"Sistema inválido em \"Sistemas\": \"{item}\" -- use o formato NomeDoSistema:NomeDoExecutavel.exe (ex.: B_Vendas:B_Vendas.exe).";
        }

        if (string.IsNullOrWhiteSpace(_campoApiToken.Valor)) return "Preencha o Token da API.";
        if (string.IsNullOrWhiteSpace(_campoDbPassword.Valor)) return "Preencha a Senha do Firebird.";
        if (!string.IsNullOrWhiteSpace(_campoDbPort.Valor) && !int.TryParse(_campoDbPort.Valor.Trim(), out _))
            return "Porta do Firebird precisa ser um número (ex.: 3050).";

        return null;
    }

    private void EscreverIni()
    {
        var linhas = new List<string>
        {
            "; Gerado pela tela de configuração do Atualizador ERP -- NUNCA commitar (tem credencial de verdade).",
            $"; Última alteração: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            "",
            $"CODIGO_CLIENTE={_campoCodigoCliente.Valor.Trim()}",
            $"SISTEMAS={LinhaUnica(_campoSistemas.Valor)}",
        };

        void Opcional(string chave, string valorBruto)
        {
            string valor = LinhaUnica(valorBruto);
            if (valor.Length > 0) linhas.Add($"{chave}={valor}");
        }

        Opcional("SISTEMAS_COM_SCRIPT", _campoSistemasComScript.Valor);
        Opcional("SCRIPTS_IGNORADOS", _campoScriptsIgnorados.Valor);
        linhas.Add($"API_TOKEN={_campoApiToken.Valor.Trim()}");
        linhas.Add($"DB_PASSWORD={_campoDbPassword.Valor.Trim()}");
        Opcional("API_URL", _campoApiUrl.Valor);
        Opcional("DB_USER", _campoDbUser.Valor);
        Opcional("DB_PORT", _campoDbPort.Valor);
        Opcional("GFIX_PATH", _campoGfixPath.Valor);
        Opcional("GBAK_PATH", _campoGbakPath.Valor);
        Opcional("ISQL_PATH", _campoIsqlPath.Valor);
        Opcional("JUNIOR_FDB", _campoJuniorFdb.Valor);
        Opcional("BEXE_FDB", _campoBexeFdb.Valor);
        Opcional("PASTA_TRABALHO", _campoPastaTrabalho.Valor);
        Opcional("PASTA_BACKUPS", _campoPastaBackups.Valor);
        Opcional("BACKUPS_PARA_MANTER", _campoBackupsParaManter.Valor);

        File.WriteAllLines(_caminhoIni, linhas, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    // O parser (ConfiguracaoAgente.LerIni) lê "CHAVE=valor" POR LINHA -- um Enter de verdade
    // digitado dentro de um campo multilinha (Sistemas, Scripts ignorados) quebraria o arquivo
    // gerado em duas linhas inválidas. Os dois campos guardam listas separadas por vírgula, então
    // remover a quebra de linha não perde informação nenhuma.
    private static string LinhaUnica(string valor) => valor.Replace("\r\n", "").Replace("\n", "").Trim();

    private async void BotaoSalvar_Click(object? sender, EventArgs e)
    {
        string? erro = ValidarCampos();
        if (erro != null)
        {
            MostrarStatus(erro, Tema.Vermelho);
            return;
        }

        _botaoSalvar.Enabled = false;
        _botaoCancelar.Enabled = false;

        try
        {
            EscreverIni();
        }
        catch (Exception ex)
        {
            MostrarStatus($"Falha ao salvar atualizador.ini: {ex.Message}", Tema.Vermelho);
            _botaoSalvar.Enabled = true;
            _botaoCancelar.Enabled = true;
            return;
        }

        if (!InstaladorServico.RodandoComoAdministrador())
        {
            MostrarStatus("Configuração salva. Pedindo permissão de Administrador pra instalar o serviço...", Tema.TextoSub);
            if (InstaladorServico.TentarRelancarElevado(out var erroElevacao))
            {
                MostrarStatus("A instalação continua numa janela elevada -- pode fechar esta.", Tema.Verde);
                await Task.Delay(2200);
                Close();
                return;
            }

            MostrarStatus(erroElevacao ?? "Não consegui pedir elevação.", Tema.Vermelho);
            _botaoSalvar.Enabled = true;
            _botaoCancelar.Enabled = true;
            return;
        }

        MostrarStatus("Instalando/reiniciando o serviço...", Tema.TextoSub);
        string caminhoExe = Environment.ProcessPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AtualizadorERP.exe");
        var resultado = await Task.Run(() => InstaladorServico.InstalarOuReiniciar(caminhoExe));

        MostrarStatus(resultado.Mensagem, resultado.Sucesso ? Tema.Verde : Tema.Vermelho);
        _botaoSalvar.Enabled = true;
        _botaoCancelar.Text = "Fechar";
        _botaoCancelar.Enabled = true;
    }

    private void MostrarStatus(string mensagem, Color cor)
    {
        _rotuloStatus.Text = mensagem;
        _rotuloStatus.ForeColor = cor;
    }
}
