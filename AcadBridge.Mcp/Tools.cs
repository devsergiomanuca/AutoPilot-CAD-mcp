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
        "read_geometry", "draw_entities", "generate_elevation",   // v0.4
        "edit_entities", "select_entities", "zoom_to",             // v0.5
        "hatch_regions",                                           // v0.5.1
        "analyze_plan",                                            // v0.6
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
             "extensão do Model (min/max/size calculados pelas entidades, nas unidades do desenho), contagem de " +
             "entidades do Model por tipo, layouts, nº de layers/blocos/estilos. " +
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
                 ["width"] = new JsonObject { ["type"] = "integer", ["minimum"] = 256, ["maximum"] = 2048, ["description"] = "Largura máxima em px (padrão 1024; aumente só se precisar ler detalhes)." },
                 ["height"] = new JsonObject { ["type"] = "integer", ["minimum"] = 256, ["maximum"] = 2048, ["description"] = "Altura máxima em px (padrão 640)." },
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

        // ---------------- v0.4: geometria, desenho e vistas ----------------
        Tool("read_geometry",
             "Lê a GEOMETRIA do desenho como o usuário vê: entra em blocos e XREFs (plantas costumam estar num XREF base " +
             "ou num bloco 'PAVIMENTO') e devolve coordenadas reais (WCS): linhas, polilinhas (pontos), arcos, círculos, " +
             "textos, cotas, hachuras, com layer efetiva e origem (xref:/block:). Filtre por região, seleção, layers e tipos. " +
             "ATENÇÃO: em desenhos reais nomes de layers/blocos podem enganar (paredes na layer 0, porta num bloco 'cadeira'): " +
             "interprete pela geometria. Traz também um palpite de unidade (o INSUNITS do arquivo costuma estar errado). " +
             "Saída compacta por padrão (uma linha por objeto, com o handle do objeto do Model; legenda no resultado). " +
             "Comece filtrando por region e types: desenhos reais têm milhares de objetos.",
             new JsonObject
             {
                 ["region"] = new JsonObject
                 {
                     ["type"] = "object",
                     ["description"] = "Janela retangular em coordenadas do desenho: {min:[x,y], max:[x,y]}.",
                     ["properties"] = new JsonObject { ["min"] = Point2(), ["max"] = Point2() },
                     ["required"] = new JsonArray("min", "max"),
                 },
                 ["room"] = RoomProp("Alternativa à região: ler só este ambiente (id ou nome do analyze_plan)."),
                 ["use_selection"] = new JsonObject { ["type"] = "boolean", ["description"] = "Ler só os objetos selecionados no AutoCAD." },
                 ["layers"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Filtro de layers com curingas (* ?), ex.: ['CE- Alvenaria', '*Esquadria*']." },
                 ["types"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Tipos DXF, ex.: ['LINE','LWPOLYLINE','ARC','MTEXT']." },
                 ["include_blocks"] = new JsonObject { ["type"] = "boolean", ["description"] = "Entrar em blocos e XREFs (padrão true)." },
                 ["include_hidden"] = new JsonObject { ["type"] = "boolean", ["description"] = "Incluir layers desligadas/congeladas (padrão false)." },
                 ["max_items"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 3000, ["description"] = "Máximo de itens detalhados (padrão 300). As contagens cobrem tudo." },
                 ["format"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("compact", "full"), ["description"] = "compact (padrão, uma linha por objeto, ~4x menos texto) ou full (JSON detalhado por objeto)." },
             }),

        Tool("draw_entities",
             "ALTERA O DESENHO (só acrescenta). Desenha um LOTE de objetos no Model numa única ação — um U desfaz o lote " +
             "inteiro. Tipos: line {start,end}, polyline {points,closed}, rectangle {corner1,corner2}, circle {center,radius}, " +
             "arc {center,radius,start_angle,end_angle em graus}, text {position,text,height,rotation,justify: left|center|right}, " +
             "mtext {position,text,height,width}, dimension {p1,p2,dim_line_point,rotation (0=horizontal, 90=vertical; " +
             "omitido=alinhada),text,text_height (opcional; sem ele o tamanho vem do DIMSCALE atual e é corrigido se ficar desproporcional às medidas)}, hatch {points,pattern (SOLID, ANSI31...),scale,color}, block {name,position,scale," +
             "rotation,attributes{TAG:valor}}. Cada item aceita layer e color (sem color = ByLayer, a cor da layer). Coordenadas nas unidades do desenho " +
             "(confira com read_geometry). Valida tudo antes: um item inválido = nada é desenhado. Layers que não existem " +
             "são criadas. Nunca desenhe por cima do desenho existente sem o usuário pedir.",
             new JsonObject
             {
                 ["entities"] = new JsonObject
                 {
                     ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 2000,
                     ["items"] = new JsonObject { ["type"] = "object", ["required"] = new JsonArray("type") },
                     ["description"] = "Lista de objetos, cada um com 'type' e seus campos (veja a descrição da ferramenta).",
                 },
                 ["layer"] = new JsonObject { ["type"] = "string", ["description"] = "Layer padrão do lote (padrão 'IA-DESENHO')." },
             }, Kind.Additive, "entities"),

        Tool("generate_elevation",
             "ALTERA O DESENHO (só acrescenta), exceto com dry_run=true. Gera a VISTA (elevação) de uma parede a partir da " +
             "planta: contorno com pé-direito, portas, janelas e vãos, paredes laterais em corte, forro, rodapé, acabamentos " +
             "(pintura/revestimento com chamada), cotas e título, ao lado da planta em layers IA-VISTA*. " +
             "Entrada: uma linha sobre a FACE da parede, da ESQUERDA para a DIREITA de quem olha a parede de dentro do " +
             "ambiente — o usuário desenha essa linha (com OSNAP nos cantos) e a deixa selecionada (use_selection=true), " +
             "ou você passa wall_line. Paredes e vãos são reconhecidos pela geometria (entra em XREFs/blocos); o pé-direito " +
             "vem do texto 'P.D.' do ambiente ou de height. FLUXO OBRIGATÓRIO: 1) chame com dry_run=true e mostre ao " +
             "usuário as aberturas (tipo, largura, alturas), pé-direito e avisos; 2) corrija com 'openings' e 'height' " +
             "conforme ele disser; 3) chame sem dry_run. Alturas e larguras dos parâmetros são em METROS; coordenadas em " +
             "unidades do desenho. Um U desfaz a vista inteira.",
             new JsonObject
             {
                 ["use_selection"] = new JsonObject { ["type"] = "boolean", ["description"] = "Usar a LINHA selecionada no AutoCAD como linha da parede." },
                 ["room"] = RoomProp("Alternativa à linha: o ambiente (id ou nome do analyze_plan). Use junto com 'wall'."),
                 ["wall"] = new JsonObject { ["type"] = "string", ["description"] = "Com 'room': a parede pelo id do analyze_plan ('W2'), pelo lado ('em cima', 'esquerda'...) ou pela abertura ('janela', 'porta'). A linha e o lado do observador saem do mapa." },
                 ["wall_line"] = new JsonObject
                 {
                     ["type"] = "object",
                     ["description"] = "Linha sobre a face da parede {start:[x,y], end:[x,y]}, da esquerda para a direita de quem olha de dentro do ambiente.",
                     ["properties"] = new JsonObject { ["start"] = Point2(), ["end"] = Point2() },
                     ["required"] = new JsonArray("start", "end"),
                 },
                 ["viewer_point"] = Point2("Opcional: um ponto dentro do ambiente; se a linha estiver ao contrário, ela é invertida."),
                 ["flip"] = new JsonObject { ["type"] = "boolean", ["description"] = "Inverte o sentido da linha (olhar a parede pelo outro lado)." },
                 ["dry_run"] = new JsonObject { ["type"] = "boolean", ["description"] = "Só analisa e devolve o que seria desenhado (nada é alterado). Use sempre primeiro." },
                 ["height"] = new JsonObject { ["type"] = "number", ["description"] = "Pé-direito em metros (ex.: 2.60). Se omitido, lê o 'P.D.' do ambiente." },
                 ["units"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("m", "cm", "mm"), ["description"] = "Unidade do desenho, se a dedução falhar ou estiver errada." },
                 ["door_height"] = new JsonObject { ["type"] = "number", ["description"] = "Altura padrão de portas e vãos em metros (padrão 2.10)." },
                 ["window_sill"] = new JsonObject { ["type"] = "number", ["description"] = "Peitoril padrão das janelas em metros (padrão 1.10)." },
                 ["window_head"] = new JsonObject { ["type"] = "number", ["description"] = "Altura do topo das janelas em metros (padrão 2.10)." },
                 ["openings"] = new JsonObject
                 {
                     ["type"] = "array",
                     ["description"] = "Correções por abertura (índices do dry_run): [{index, kind: porta|janela|vão|parede, sill|bottom, head|top (m), label}]. kind 'parede' remove a abertura.",
                     ["items"] = new JsonObject { ["type"] = "object", ["required"] = new JsonArray("index") },
                 },
                 ["origin"] = Point2("Onde desenhar a vista (canto inferior esquerdo, coordenadas do desenho). Padrão: abaixo de tudo o que existe."),
                 ["ceiling_height"] = new JsonObject { ["type"] = "number", ["description"] = "Altura do FORRO em metros, se houver forro abaixo da laje (ex.: 2.46). 'height' continua sendo a laje/pé-direito estrutural." },
                 ["ceiling_thickness"] = new JsonObject { ["type"] = "number", ["description"] = "Espessura do forro em metros (padrão 0.02)." },
                 ["baseboard"] = new JsonObject { ["type"] = "number", ["description"] = "Altura do rodapé em metros (ex.: 0.07). Omitido = sem rodapé." },
                 ["side_walls"] = new JsonObject { ["type"] = "boolean", ["description"] = "Desenhar as paredes laterais em corte, hachuradas (padrão true)." },
                 ["side_wall_thickness"] = new JsonObject { ["type"] = "number", ["description"] = "Espessura das paredes laterais em metros (padrão: a da parede analisada)." },
                 ["finishes"] = new JsonObject
                 {
                     ["type"] = "array",
                     ["description"] = "Acabamentos (pintura, revestimento, painel) como áreas na parede, hachuradas e com chamada de texto: " +
                                       "[{label: 'Tinta acrílica cor X | acetinado', from, to (m ao longo da parede, padrão 0..comprimento), " +
                                       "bottom, top (m do piso, padrão rodapé..forro), color (ACI, 'R,G,B' ou nome), pattern (padrão SOLID)}]. " +
                                       "Portas e janelas viram furos na hachura. Os limites entram nas cotas.",
                     ["items"] = new JsonObject { ["type"] = "object" },
                 },
                 ["title"] = new JsonObject { ["type"] = "string", ["description"] = "Título da vista (ex.: 'VISTA 7 - COZINHA')." },
                 ["layer_prefix"] = new JsonObject { ["type"] = "string", ["description"] = "Prefixo das layers criadas (padrão 'IA-VISTA')." },
             }, Kind.Additive),

        // ---------------- v0.5: editar o que já existe ----------------
        Tool("edit_entities",
             "ALTERA O DESENHO (pode mudar e APAGAR objetos existentes), exceto com dry_run=true. Edita objetos do Model " +
             "apontados por HANDLE (o read_geometry devolve o handle de cada item). Lote de operações aplicadas em ordem: " +
             "move {displacement:[dx,dy] ou from,to}, copy {displacement ou from,to, count}, rotate {base, angle em graus " +
             "anti-horário}, scale {base, factor}, mirror {p1, p2, keep_original}, erase, set_props {layer, color (ACI, nome ou 'bylayer')}, " +
             "set_text {text} (TEXT/MTEXT/cota), set_attributes {attributes:{TAG:valor}} (blocos). " +
             "FLUXO OBRIGATÓRIO: 1) dry_run=true e mostre ao usuário a lista de objetos afetados (use select_entities para " +
             "destacá-los na tela); 2) só com o ok dele, chame sem dry_run. Recusa objetos em layer travada, fora do Model " +
             "ou dentro de blocos/XREFs (o handle de um item de XREF é a inserção do XREF inteiro: exige allow_xref). " +
             "Coordenadas nas unidades do desenho. Um U desfaz o lote inteiro.",
             new JsonObject
             {
                 ["operations"] = new JsonObject
                 {
                     ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 200,
                     ["description"] = "Lista de {action, handles:[...], ...parâmetros da ação}. Ex.: " +
                                       "{action:'move', handles:['1B00D'], displacement:[0.5, 0]}.",
                     ["items"] = new JsonObject { ["type"] = "object", ["required"] = new JsonArray("action", "handles") },
                 },
                 ["dry_run"] = new JsonObject { ["type"] = "boolean", ["description"] = "Só valida e lista o que seria alterado (nada muda, sem undo). Use sempre primeiro." },
                 ["allow_xref"] = new JsonObject { ["type"] = "boolean", ["description"] = "Permite mover/apagar a INSERÇÃO de um XREF (a planta inteira). Só se o usuário pediu explicitamente." },
             }, Kind.Modifies, "operations"),

        Tool("hatch_regions",
             "ALTERA O DESENHO (só acrescenta hachuras), exceto com dry_run=true. Hachura áreas FECHADAS achando o contorno " +
             "calculando o contorno pela geometria (como clicar dentro da área no comando HATCH, mas enxergando blocos e XREFs) — NÃO monte polígonos de " +
             "hachura à mão com draw_entities a partir de linhas soltas. Modos: 'walls' = hachura as PAREDES sozinho " +
             "(acha pares de linhas paralelas com espessura de parede numa região ou na seleção do usuário, entra em " +
             "blocos/XREFs, e REJEITA contornos que vazaram por parede aberta, dizendo onde); 'points' = um ponto DENTRO " +
             "de cada área (ambiente, piso, peça; a região é deduzida dos pontos). FLUXO: dry_run=true → diga quantas áreas e mostre as rejeitadas (o " +
             "usuário pode fechar a parede com uma linha) → com o ok, chame sem dry_run passando a 'region' que o dry_run devolveu. Um U desfaz todas as hachuras.",
             new JsonObject
             {
                 ["mode"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("walls", "points"), ["description"] = "walls (padrão sem 'points') ou points." },
                 ["points"] = new JsonObject { ["type"] = "array", ["items"] = Point2(), ["description"] = "Modo points: um ponto [x,y] dentro de cada área." },
                 ["region"] = new JsonObject
                 {
                     ["type"] = "object",
                     ["description"] = "Região de trabalho {min:[x,y], max:[x,y]} (obrigatória no modo walls, se não usar a seleção; no modo points é deduzida dos pontos). O dry_run devolve a região usada: repita-a na execução.",
                     ["properties"] = new JsonObject { ["min"] = Point2(), ["max"] = Point2() },
                     ["required"] = new JsonArray("min", "max"),
                 },
                 ["use_selection"] = new JsonObject { ["type"] = "boolean", ["description"] = "Usar a extensão dos objetos selecionados pelo usuário como região." },
                 ["rooms"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Alternativa a 'points': ambientes por id ou nome do analyze_plan (ex.: ['BWC','R3']). Vira o modo points." },
                 ["layers"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Modo walls: só linhas destas layers (curingas * ?), se as paredes tiverem layer própria." },
                 ["min_thickness"] = new JsonObject { ["type"] = "number", ["description"] = "Modo walls: espessura mínima de parede em metros (padrão 0.07)." },
                 ["max_thickness"] = new JsonObject { ["type"] = "number", ["description"] = "Modo walls: espessura máxima em metros (padrão 0.40)." },
                 ["units"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("m", "cm", "mm"), ["description"] = "Unidade do desenho, se a dedução falhar." },
                 ["close_doors"] = new JsonObject { ["type"] = "boolean", ["description"] = "Modo points: tratar portas de giro como FECHADAS para o contorno não sair pelo vão (padrão true)." },
                 ["include_nested"] = new JsonObject { ["type"] = "boolean", ["description"] = "Modo walls: hachurar também contornos de blocos DENTRO do bloco das paredes (por padrão são rejeitados: costumam ser janelas/portas/pilares)." },
                 ["pattern"] = new JsonObject { ["type"] = "string", ["description"] = "Padrão: SOLID (padrão), ANSI31, AR-CONC..." },
                 ["scale"] = new JsonObject { ["type"] = "number", ["description"] = "Escala do padrão (não vale para SOLID)." },
                 ["color"] = new JsonObject { ["type"] = new JsonArray("string", "integer"), ["description"] = "Cor: ACI, 'R,G,B', nome ou 'bylayer' (padrão: ByLayer, a cor da layer)." },
                 ["layer"] = new JsonObject { ["type"] = "string", ["description"] = "Layer das hachuras (padrão 'IA-HACHURA'; criada se não existir)." },
                 ["dry_run"] = new JsonObject { ["type"] = "boolean", ["description"] = "Só acha os contornos e informa (nada é desenhado). Use sempre primeiro." },
             }, Kind.Additive),

        Tool("select_entities",
             "Seleciona (com grips) objetos do Model por handle no AutoCAD e, por padrão, dá zoom neles, para o usuário " +
             "VER quais são antes de confirmar uma edição. Não altera o desenho (o zoom entra no histórico de undo, como " +
             "qualquer zoom).",
             new JsonObject
             {
                 ["handles"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Handles (do read_geometry)." },
                 ["zoom"] = new JsonObject { ["type"] = "boolean", ["description"] = "Enquadrar os objetos (padrão true)." },
                 ["margin"] = new JsonObject { ["type"] = "number", ["description"] = "Folga em fração do tamanho (padrão 0.15)." },
             }, Kind.ReadOnly, "handles"),

        Tool("zoom_to",
             "Enquadra a tela do AutoCAD numa região (coordenadas do desenho) ou em objetos por handle. Use antes de " +
             "capture_view para ver de perto o que leu ou desenhou. Não altera o desenho (o zoom entra no histórico de undo).",
             new JsonObject
             {
                 ["region"] = new JsonObject
                 {
                     ["type"] = "object",
                     ["description"] = "{min:[x,y], max:[x,y]}.",
                     ["properties"] = new JsonObject { ["min"] = Point2(), ["max"] = Point2() },
                     ["required"] = new JsonArray("min", "max"),
                 },
                 ["handles"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Alternativa à região: enquadrar estes objetos." },
                 ["margin"] = new JsonObject { ["type"] = "number", ["description"] = "Folga em fração do tamanho (padrão 0.1)." },
                 ["room"] = RoomProp("Alternativa: enquadrar este ambiente (id ou nome do analyze_plan)."),
             }),

        // ---------------- v0.6: mapa da planta ----------------
        Tool("analyze_plan",
             "Lê a planta UMA VEZ e devolve um MAPA curto: ambientes (id R1.., nome e área pelos textos, área calculada), " +
             "paredes de cada ambiente (id W1.., lado, comprimento, espessura, janelas/portas/vãos com posição), portas " +
             "(largura, quais ambientes liga, para onde abre). Entra em blocos/XREFs; ignora o que a IA desenhou (layers IA-*). " +
             "USE ANTES de trabalhar numa planta: depois as ferramentas aceitam nomes no lugar de coordenadas — " +
             "hatch_regions {rooms}, zoom_to {room}, read_geometry {room}, generate_elevation {room, wall}. Muito mais " +
             "rápido e barato que ler linhas com read_geometry. O mapa fica guardado e é refeito sozinho se a planta mudar. " +
             "Não altera o desenho.",
             new JsonObject
             {
                 ["region"] = new JsonObject
                 {
                     ["type"] = "object",
                     ["description"] = "Região que cobre a planta inteira (um pavimento ou apartamento) {min:[x,y], max:[x,y]}. Ambientes cortados pela borda ficam de fora.",
                     ["properties"] = new JsonObject { ["min"] = Point2(), ["max"] = Point2() },
                     ["required"] = new JsonArray("min", "max"),
                 },
                 ["use_selection"] = new JsonObject { ["type"] = "boolean", ["description"] = "Usar a extensão do que o usuário selecionou (ex.: o bloco do pavimento) como região." },
                 ["units"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("m", "cm", "mm"), ["description"] = "Unidade do desenho, se a dedução falhar." },
                 ["walls"] = new JsonObject { ["type"] = "boolean", ["description"] = "Analisar paredes e aberturas de cada ambiente (padrão true; false = só ambientes e portas, mais rápido)." },
                 ["layers"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Só estas layers (curingas * ?), se a planta tiver layers confiáveis." },
             }, Kind.ReadOnly),
    };

    private static JsonObject RoomProp(string description) => new() { ["type"] = "string", ["description"] = description };

    private static JsonObject Point2(string? description = null)
    {
        var o = new JsonObject
        {
            ["type"] = "array", ["minItems"] = 2, ["maxItems"] = 3,
            ["items"] = new JsonObject { ["type"] = "number" },
        };
        if (description != null) o["description"] = description;
        return o;
    }


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
