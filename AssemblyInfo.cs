using System.Runtime.CompilerServices;

// Só para o teste de integração chamar Worker.ProcessarAtualizacao (internal) direto, sem
// duplicar a orquestração da Fase 3/4 dentro do teste -- ver AtualizadorERP.Tests/WorkerIntegrationTests.cs.
[assembly: InternalsVisibleTo("AtualizadorERP.Tests")]
