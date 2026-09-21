using AtualizadorERP;
using AtualizadorERP.Services;
using AtualizadorERP.UI;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;

internal static class ProgramaPrincipal
{
    // [STAThread] exige um Main explícito -- não dá pra anotar o Main implícito de uma statement
    // top-level. WinForms (Application.Run/MessageBox, usados nos dois ramos interativos abaixo)
    // depende de uma thread STA pra Clipboard/drag-drop/diálogos comuns funcionarem direito.
    [STAThread]
    private static void Main(string[] args)
    {
        // Ramo "pós-elevação": o próprio .exe se relançou como Administrador (ver
        // InstaladorServico.TentarRelancarElevado, chamado pelo SetupForm depois de já ter salvo
        // o atualizador.ini) só pra registrar/reiniciar o serviço, que exige elevação. Sem tela:
        // o .ini já foi preenchido pelo processo não-elevado que disparou este.
        if (InstaladorServico.FoiChamadoPosElevacao(args))
        {
            RodarInstalacaoElevada();
            return;
        }

        // Fora do SCM (clique duplo no .exe) -- mostra a tela de configuração em vez de subir o
        // Worker em primeiro plano. Cobre tanto a primeira instalação (sem atualizador.ini ainda)
        // quanto uma reconfiguração de um agente já instalado.
        if (!WindowsServiceHelpers.IsWindowsService())
        {
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm());
            return;
        }

        RodarComoServico(args);
    }

    private static void RodarInstalacaoElevada()
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        string caminhoExe = Environment.ProcessPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AtualizadorERP.exe");
        var resultado = InstaladorServico.InstalarOuReiniciar(caminhoExe);
        MessageBox.Show(
            resultado.Mensagem,
            "Atualizador ERP",
            MessageBoxButtons.OK,
            resultado.Sucesso ? MessageBoxIcon.Information : MessageBoxIcon.Error);
    }

    private static void RodarComoServico(string[] args)
    {
        IHost host = Host.CreateDefaultBuilder(args)
            .UseWindowsService(options =>
            {
                options.ServiceName = "Agente Atualizador ERP";
            })
            .ConfigureServices((hostContext, services) =>
            {
                services.AddSingleton<ConfiguracaoAgente>();
                services.AddHostedService<Worker>();
                services.AddSingleton<ApiService>();
                services.AddSingleton<DatabaseService>();
                services.AddSingleton<ExtractionService>();
                services.AddSingleton<ProcessService>();
                services.AddSingleton<ScriptRunnerService>();
            })
            .Build();

        host.Run();
    }
}
