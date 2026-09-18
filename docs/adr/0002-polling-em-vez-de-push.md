# ADR-0002 — Polling de um campo no banco do cliente, em vez de push

**Situação:** Aceita

## Contexto

O agente precisa saber duas coisas: **se existe versão nova** (informação que
mora no servidor central) e **se o usuário autorizou aplicar** (decisão tomada
no ERP Delphi, que roda na máquina do cliente).

O agente vive atrás do NAT do cliente, sem IP fixo, sem porta aberta para a
internet.

O ERP Delphi é um sistema legado de terceiros, que **não está neste
repositório** e não pode ser alterado para chamar uma API.

## Decisão

O agente faz **polling** e reage ao campo `STATUS` da linha correspondente ao
sistema, na tabela `SYS_ATUALIZACAO` do `JUNIOR.fdb` do próprio cliente.

O banco do cliente é o ponto de encontro entre dois programas que não se
conhecem: o agente escreve `PENDENTE`; o ERP Delphi, quando o usuário autoriza,
escreve `AUTORIZADO`; o agente vê e prossegue.

A tabela não existe no schema real do ERP — o próprio agente a cria e garante
uma linha por sistema instalado (`DatabaseService.GarantirTabelaSysAtualizacao`).

## Consequências

**Ganhos**
- Funciona sem nenhuma porta aberta no cliente: toda conexão parte de dentro.
- Não exige alterar o ERP Delphi, que é o que torna a Fase 2 possível.
- O estado é durável e inspecionável: dá para abrir o banco do cliente e ver
  exatamente em que ponto o ciclo está, mesmo depois de o serviço reiniciar.
- Reinício do serviço no meio do processo não perde o estado — ele é retomado a
  partir do que está gravado, não do que estava em memória.

**Custos aceitos**
- **Latência.** Uma versão publicada agora só é notada no próximo ciclo. Aceito:
  atualização de ERP não é operação de segundos.
- Consulta periódica ao banco do cliente, para sempre. Mitigado por ser uma
  leitura trivial por chave primária, com intervalo espaçado.
- O contrato entre agente e ERP é um **campo de texto num banco**, não uma
  interface tipada. Mudar os valores possíveis de `STATUS` exige coordenar com
  um sistema que não controlamos. Por isso os estados estão documentados em
  tabela no [README](../../README.md) — é o contrato real.

## Alternativas consideradas

- **Webhook / push do servidor central para o agente** — impossível sem IP
  público e porta aberta em cada cliente.
- **WebSocket / SignalR com conexão persistente do agente** — descartado:
  resolveria a latência da parte "existe versão nova", mas não a Fase 2 (a
  autorização vem do ERP Delphi, não do servidor central), então a leitura do
  banco continuaria necessária. Acrescentaria uma conexão permanente para
  manter, sem eliminar o polling.
- **Arquivo de sinalização em disco** — descartado: o banco já é transacional e
  já está aberto pelos dois programas. Um arquivo traria problemas de escrita
  concorrente que o Firebird já resolve.
