# Skill: Consulta com CTE Recursivo (Auto-Relacionamento)

## Quando usar

Use esta skill quando o usuário precisar de uma consulta SQL com CTE recursivo para tabelas que possuem auto-relacionamento (uma coluna que referencia a própria tabela, como `IDPAI → ID`).

## Estrutura esperada da tabela

- Uma coluna de ID (PK) identifica cada registro
- Uma coluna de IDPAI (FK) referencia o ID do registro pai

## Exemplo prático (MTAREFA)

Tabela com auto-relacionamento: `IDPAI` → `IDTRF` (ambos na mesma tabela `MTAREFA`).
Pedido: Listar a hierarquia de todas as tarefas da tarefa 126 do projeto 2 e coligada 1.

```sql server
WITH CTE_RECURSIVO AS (
    -- Âncora: a própria tarefa raiz (opcional, ou pode começar só pelos filhos)
    SELECT
        CODCOLIGADA,
        IDPRJ,
        IDTRF,
        CODTRF,
        NOME,
        IDPAI,
        0 AS NIVEL
    FROM MTAREFA (NOLOCK)
    WHERE CODCOLIGADA = 1
      AND IDPRJ = 2
      AND IDTRF = 126    

    UNION ALL

    -- Passo recursivo: busca os filhos de cada tarefa encontrada
    SELECT
        t.CODCOLIGADA,
        t.IDPRJ,
        t.IDTRF,
        t.CODTRF,
        t.NOME,
        t.IDPAI,
        c.NIVEL + 1 AS NIVEL
    FROM MTAREFA t (NOLOCK)
    INNER JOIN CTE_RECURSIVO c
        ON t.CODCOLIGADA = c.CODCOLIGADA
       AND t.IDPRJ = c.IDPRJ
       AND t.IDPAI = c.IDTRF
       AND t.IDPAI <> t.IDTRF -- a tarefa raiz tem os 2 campos iguais
    WHERE
        c.NIVEL < 10 -- Evita recursão infinita
)
SELECT
    CODCOLIGADA,
    IDPRJ,
    IDTRF,
    CODTRF,
    NOME,
    IDPAI,
    NIVEL
FROM CTE_RECURSIVO
ORDER BY 
    CODTRF
OPTION (MAXRECURSION 10)
```

## Regras importantes

1. **`t.IDPAI <> t.IDTRF`** é necessário para evitar recursão infinita
2. **`c.NIVEL < 10`** é necessário para evitar recursão infinita
3. **`OPTION (MAXRECURSION 10)`** é necessário para evitar recursão infinita
4. **Sempre use `UNION ALL`** entre âncora e parte recursiva
5. **Coluna `NIVEL`** obrigatória para rastrear profundidade (incrementar com `+ 1`)
6. **Chaves compostas**: quando a PK for composta, inclua TODAS as colunas no JOIN recursivo
7. **`(NOLOCK)`** recomendado para consultas de leitura em produção
8. **ORDER BY** por código hierárquico para preservar a estrutura de árvore
