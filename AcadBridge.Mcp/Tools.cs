using System.Linq;
using System.Text.Json.Nodes;

namespace AcadBridge.Mcp;

/// <summary>
/// Catálogo de ferramentas anunciado em tools/list. A "description" é o que o modelo (Claude/Codex)
/// lê para decidir QUANDO usar cada ferramenta — escrever bem aqui importa tanto quanto o código.
/// </summary>
internal static class Tools
{
    /// <summary>Ferramentas repassadas 1:1 para o plugin dentro do AutoCAD.</summary>
    public static readonly HashSet<string> BridgeMethods = new()
    {
        "list_documents", "get_drawing_summary", "list_layers", "list_blocks", "get_selection", "capture_view",
        "create_layer", "update_layer", "move_selection_to_layer",
    };

    public static JsonArray Definitions() => new()
    {
        Tool("acad_status",
             "Verifica se há um AutoCAD 2025 aberto com o plugin AcadBridge ativo (pid, porta, versão, edição liberada) " +
             "e em qual desenho esta conversa está fixada.",
             new JsonObject()),

        // ---------------- v0.3: vários desenhos abertos ----------------
        Tool("list_documents",
             "Lista os desenhos abertos no AutoCAD (abas): id, nome, caminho (null = nunca salvo), qual está ATIVO e " +
             "em qual esta conversa está fixada. Use quando houver dúvida sobre qual desenho o usuário quer, ou quando " +
             "uma chamada for recusada por 'aba ativa diferente'.",
             new JsonObject()),

        Tool("set_target_document",
             "Fixa esta conversa num desenho aberto (por id de list_documents ou pelo nome do arquivo). Todas as " +
             "outras ferramentas passam a ser RECUSADAS se a aba ativa não for esse desenho — proteção contra " +
             "ler/alterar o desenho errado. A primeira chamada da conversa já fixa a aba ativa automaticamente; use " +
             "esta ferramenta só quando o USUÁRIO disser que quer trabalhar em outro desenho. Não troca a aba no " +
             "AutoCAD (quem troca é o usuário).",
             new JsonObject
             {
                 ["document_id"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["description"] = "id do desenho (veja list_documents)." },
                 ["document"] = new JsonObject { ["type"] = "string", ["description"] = "Alternativa ao id: nome do arquivo (ex.: 'Planta.dwg') ou caminho completo." },
             }),

        Tool("get_drawing_summary",
             "Resumo do desenho ativo: nome/arquivo, unidades, alterações não salvas, layer atual, " +
             "extensão do desenho, contagem de entidades do Model por tipo, layouts, nº de layers/blocos/estilos. " +
             "Use primeiro para se orientar.",
             new JsonObject()),

        Tool("list_layers",
             "Lista todas as layers com estado (ligada, congelada, travada, plotável), cor, tipo de linha, " +
             "espessura, descrição e quantas entidades cada uma tem.",
             new JsonObject
             {
                 ["include_counts"] = new JsonObject
                 {
                     ["type"] = "boolean",
                     ["description"] = "Contar entidades por layer (padrão true; pode demorar em desenhos enormes).",
                 },
             }),

        Tool("list_blocks",
             "Lista as definições de bloco (sem layouts/anônimos) com nº de inserções (inclui variações de blocos " +
             "dinâmicos), se é dinâmico/xref, descrição e as definições de atributo (tag, prompt, valor padrão). " +
             "Útil para quantitativos e para conferir padrões de bloco.",
             new JsonObject()),

        Tool("get_selection",
             "Descreve os objetos que o usuário selecionou no AutoCAD: tipo, layer, cor, limites e dados " +
             "específicos (comprimento de linhas/polilinhas, área de polilinhas fechadas e hachuras, raio, " +
             "texto, valor de cotas, nome/atributos/propriedades dinâmicas de blocos).",
             new JsonObject
             {
                 ["max_items"] = new JsonObject
                 {
                     ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 500,
                     ["description"] = "Máximo de objetos descritos (padrão 100).",
                 },
             }),

        Tool("capture_view",
             "Captura uma imagem PNG da vista atual do desenho ativo, para você VER o que o usuário está vendo. " +
             "Combine com get_selection/get_drawing_summary para medidas exatas (a imagem não é precisa para medir). " +
             "width/height são o tamanho MÁXIMO: a imagem mantém a proporção da janela do AutoCAD e pode sair menor.",
             new JsonObject
             {
                 ["width"] = new JsonObject { ["type"] = "integer", ["minimum"] = 256, ["maximum"] = 2048, ["description"] = "Largura máxima em px (padrão 1280)." },
                 ["height"] = new JsonObject { ["type"] = "integer", ["minimum"] = 256, ["maximum"] = 2048, ["description"] = "Altura máxima em px (padrão 800)." },
             }),

        // ---------------- v0.2: edição (cada chamada = 1 grupo de undo no AutoCAD) ----------------
        Tool("create_layer",
             "ALTERA O DESENHO. Cria uma layer nova com as propriedades informadas (cor, tipo e espessura de linha, " +
             "plotável, descrição) e opcionalmente a torna atual. Falha se já existir (use update_layer). " +
             "Antes de criar várias layers, confira os padrões existentes com list_layers. " +
             "O usuário desfaz com um único U no AutoCAD.",
             LayerProps(forCreate: true), Kind.Additive, "name"),

        Tool("update_layer",
             "ALTERA O DESENHO. Muda propriedades de uma layer existente: cor, tipo/espessura de linha, plotável, " +
             "descrição, ligar/desligar, congelar/descongelar, travar/destravar, tornar atual. " +
             "Só as propriedades informadas mudam; o resultado lista 'de → para'. Um único U desfaz.",
             LayerProps(forCreate: false), Kind.Modifies, "name"),

        Tool("move_selection_to_layer",
             "ALTERA O DESENHO. Move os objetos que o usuário SELECIONOU no AutoCAD para uma layer existente. " +
             "Objetos em layers travadas são pulados (o resultado informa). Confira a seleção com get_selection antes " +
             "se tiver dúvida. Um único U desfaz.",
             new JsonObject
             {
                 ["layer"] = new JsonObject { ["type"] = "string", ["description"] = "Nome da layer de destino (deve existir)." },
             }, Kind.Modifies, "layer"),
    };


    // Propriedades comuns a create_layer/update_layer.
    private static JsonObject LayerProps(bool forCreate) => new()
    {
        ["name"] = new JsonObject { ["type"] = "string", ["description"] = forCreate ? "Nome da layer." : "Nome exato de uma layer existente (veja list_layers)." },
        ["color"] = new JsonObject
        {
            ["type"] = new JsonArray("string", "integer"),
            ["description"] = "Cor: índice ACI 1–255 (1=vermelho, 2=amarelo, 3=verde, 4=ciano, 5=azul, 6=magenta, 7=branco/preto, 8/9=cinzas), 'R,G,B' (ex.: '255,128,0') ou nome básico.",
        },
        ["linetype"] = new JsonObject { ["type"] = "string", ["description"] = "Tipo de linha (ex.: Continuous, DASHED, HIDDEN, CENTER). Se não existir no desenho, é carregado de acadiso.lin." },
        ["lineweight"] = new JsonObject
        {
            ["type"] = new JsonArray("number", "string"),
            ["description"] = "Espessura em mm, valores padrão do AutoCAD (0, 0.05, 0.09, 0.13, 0.15, 0.18, 0.2, 0.25, 0.3, 0.35, 0.4, 0.5, 0.53, 0.6, 0.7, 0.8, 0.9, 1, 1.06, 1.2, 1.4, 1.58, 2, 2.11) ou 'default'.",
        },
        ["plottable"] = new JsonObject { ["type"] = "boolean", ["description"] = "Se a layer sai na impressão." },
        ["on"] = new JsonObject { ["type"] = "boolean", ["description"] = "Ligada (true) / desligada (false)." },
        ["frozen"] = new JsonObject { ["type"] = "boolean", ["description"] = "Congelada. A layer atual não pode ser congelada." },
        ["locked"] = new JsonObject { ["type"] = "boolean", ["description"] = "Travada (objetos visíveis, mas não editáveis)." },
        ["description"] = new JsonObject { ["type"] = "string", ["description"] = "Descrição da layer." },
        ["make_current"] = new JsonObject { ["type"] = "boolean", ["description"] = "Tornar esta a layer atual." },
    };

    private enum Kind { ReadOnly, Additive, Modifies }

    private static JsonObject Tool(string name, string description, JsonObject properties,
                                   Kind kind = Kind.ReadOnly, params string[] required)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false,
        };
        if (required.Length > 0) schema["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray());

        // Anotações MCP: dicas para o CLIENTE (ex.: pedir confirmação antes de ferramentas que alteram algo).
        var ann = new JsonObject { ["readOnlyHint"] = kind == Kind.ReadOnly, ["openWorldHint"] = false };
        if (kind != Kind.ReadOnly)
        {
            ann["destructiveHint"] = kind == Kind.Modifies;   // Additive = só acrescenta, não muda o que existe
            ann["idempotentHint"] = false;
        }
        return new JsonObject { ["name"] = name, ["description"] = description, ["inputSchema"] = schema, ["annotations"] = ann };
    }
}
