# Registros de Decisão de Arquitetura (ADR) — Agente

Decisões de arquitetura do agente C#, cada uma num arquivo: o que foi decidido,
em que contexto, o que se ganhou e o que se perdeu.

Este agente roda **sem supervisão, no servidor do cliente, com poder de parar o
banco de dados da empresa dele**. Quase toda decisão aqui foi tomada com a
pergunta "e se isso falhar às 3h da manhã, sem ninguém olhando?" — e o motivo
raramente é óbvio a partir do código. Daí estes documentos.

Leitura obrigatória junto destes: [RISCOS-CONHECIDOS.md](../../RISCOS-CONHECIDOS.md).

## Índice

| # | Decisão | Situação |
|---|---|---|
| [0001](0001-configuracao-em-ini.md) | Configuração em `.ini` ao lado do executável | Aceita |
| [0002](0002-polling-em-vez-de-push.md) | Polling de um campo no banco do cliente, em vez de push | Aceita |
| [0003](0003-timeout-obrigatorio.md) | Timeout obrigatório em todo processo externo | Aceita |
| [0004](0004-lista-explicita-de-sistemas-com-script.md) | Lista explícita de sistemas com script | Aceita |
| [0005](0005-uma-instancia-por-cliente.md) | Uma instância do agente por cliente, não por sistema | Aceita |

## Como escrever um novo

**Contexto → Decisão → Consequências → Alternativas consideradas.** Numere em
sequência. ADR aceito não se edita: se a decisão mudar, escreva um novo que o
substitua.
