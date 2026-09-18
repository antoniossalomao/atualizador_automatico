## O que muda, e por quê

<!-- O "o quê" o diff já mostra. Escreva o PORQUÊ. Este agente roda sem
     supervisão no servidor do cliente e pode parar o banco dele: a decisão por
     trás da mudança importa mais aqui do que na maioria dos projetos. -->

## Impacto no ciclo

<!-- Qual(is) fase(s) isso toca? Fase 3 (banco em shutdown) exige atenção
     redobrada. Se não toca em nenhuma, diga isso. -->

- [ ] Fase 1 — preparo (download, extração)
- [ ] Fase 2 — espera de autorização
- [ ] Fase 3 — **crítica** (banco parado: gfix/gbak/scripts)
- [ ] Fase 4 — distribuição
- [ ] Nenhuma (configuração, log, documentação, teste)

## Antes de pedir revisão

- [ ] `dotnet build AtualizadorERP.sln -warnaserror` passa com **0 warnings**
- [ ] `dotnet test AtualizadorERP.sln` passa — a suíte **completa**, com Firebird local, não só o subconjunto do CI
- [ ] Li [`RISCOS-CONHECIDOS.md`](../RISCOS-CONHECIDOS.md) e nada aqui contradiz o que está lá (ou atualizei o arquivo)
- [ ] Todo processo externo novo passa pelo `ProcessService` **com timeout**
- [ ] Nenhum argumento de processo montado por interpolação de string
- [ ] Nenhuma credencial nova na linha de comando (vai por variável de ambiente)
- [ ] Se há caminho que pode lançar entre `gfix -shut` e `gfix -online`: existe caminho de volta que reabre o banco
- [ ] Teste novo que usa Firebird está marcado com `[Trait("Requer", "Firebird")]`
- [ ] Se a decisão é cara de reverter: escrevi um ADR em `docs/adr/`

<!-- Item que não se aplica: risque em vez de marcar (~~texto~~) e siga. -->
