namespace Sofia.App.Services;

/// <summary>
/// Minimal localization stub for the shell chrome (the full key set and the settings tab land in spec 043). The locale
/// list and codes match the web (<c>src/Sofia.UI/src/i18n</c>): en, pt, es, fr, zh, ja, with English as the fallback.
/// </summary>
public static class Localization
{
    public static readonly string[] Languages = ["en", "pt", "es", "fr", "zh", "ja"];

    private static readonly Dictionary<string, Dictionary<string, string>> Strings = new(StringComparer.Ordinal)
    {
        ["en"] = new()
        {
            ["app.name"] = "Sofia",
            ["gate.title"] = "Connecting to Sofia",
            ["gate.probing"] = "Looking for a local server…",
            ["gate.ready"] = "Connected",
            ["gate.failed"] = "No server answered",
            ["gate.url"] = "Server URL",
            ["gate.token"] = "Access token",
            ["gate.pair"] = "Pair",
            ["gate.code"] = "Pairing code",
            ["gate.retry"] = "Try again",
            ["gate.log"] = "Connection log",
            ["shell.newChat"] = "New chat",
            ["shell.search"] = "Search conversations",
            ["shell.draft"] = "What should Sofia work on?",
            ["shell.send"] = "Send",
            ["shell.settings"] = "Settings",
            ["shell.changes"] = "Changes",
            ["shell.tasks"] = "Tasks",
            ["shell.emptyTitle"] = "Start a conversation",
            ["shell.emptyBody"] = "Pick a workspace on the left and describe the task.",
            ["shell.working"] = "Working",
        },
        ["pt"] = new()
        {
            ["app.name"] = "Sofia",
            ["gate.title"] = "Conectando ao Sofia",
            ["gate.probing"] = "Procurando um servidor local…",
            ["gate.ready"] = "Conectado",
            ["gate.failed"] = "Nenhum servidor respondeu",
            ["gate.url"] = "URL do servidor",
            ["gate.token"] = "Token de acesso",
            ["gate.pair"] = "Parear",
            ["gate.code"] = "Código de pareamento",
            ["gate.retry"] = "Tentar de novo",
            ["gate.log"] = "Registro da conexão",
            ["shell.newChat"] = "Nova conversa",
            ["shell.search"] = "Buscar conversas",
            ["shell.draft"] = "No que a Sofia deve trabalhar?",
            ["shell.send"] = "Enviar",
            ["shell.settings"] = "Configurações",
            ["shell.changes"] = "Alterações",
            ["shell.tasks"] = "Tarefas",
            ["shell.emptyTitle"] = "Comece uma conversa",
            ["shell.emptyBody"] = "Escolha um workspace à esquerda e descreva a tarefa.",
            ["shell.working"] = "Trabalhando",
        },
        ["es"] = new()
        {
            ["app.name"] = "Sofia",
            ["gate.title"] = "Conectando con Sofia",
            ["gate.probing"] = "Buscando un servidor local…",
            ["gate.ready"] = "Conectado",
            ["gate.failed"] = "Ningún servidor respondió",
            ["gate.url"] = "URL del servidor",
            ["gate.token"] = "Token de acceso",
            ["gate.pair"] = "Emparejar",
            ["gate.code"] = "Código de emparejamiento",
            ["gate.retry"] = "Reintentar",
            ["gate.log"] = "Registro de conexión",
            ["shell.newChat"] = "Nueva conversación",
            ["shell.search"] = "Buscar conversaciones",
            ["shell.draft"] = "¿En qué debe trabajar Sofia?",
            ["shell.send"] = "Enviar",
            ["shell.settings"] = "Ajustes",
            ["shell.changes"] = "Cambios",
            ["shell.tasks"] = "Tareas",
            ["shell.emptyTitle"] = "Empieza una conversación",
            ["shell.emptyBody"] = "Elige un workspace a la izquierda y describe la tarea.",
            ["shell.working"] = "Trabajando",
        },
        ["fr"] = new()
        {
            ["app.name"] = "Sofia",
            ["gate.title"] = "Connexion à Sofia",
            ["gate.probing"] = "Recherche d'un serveur local…",
            ["gate.ready"] = "Connecté",
            ["gate.failed"] = "Aucun serveur n'a répondu",
            ["gate.url"] = "URL du serveur",
            ["gate.token"] = "Jeton d'accès",
            ["gate.pair"] = "Appairer",
            ["gate.code"] = "Code d'appairage",
            ["gate.retry"] = "Réessayer",
            ["gate.log"] = "Journal de connexion",
            ["shell.newChat"] = "Nouvelle conversation",
            ["shell.search"] = "Rechercher des conversations",
            ["shell.draft"] = "Sur quoi Sofia doit-elle travailler ?",
            ["shell.send"] = "Envoyer",
            ["shell.settings"] = "Réglages",
            ["shell.changes"] = "Changements",
            ["shell.tasks"] = "Tâches",
            ["shell.emptyTitle"] = "Commencez une conversation",
            ["shell.emptyBody"] = "Choisissez un workspace à gauche et décrivez la tâche.",
            ["shell.working"] = "En cours",
        },
        ["zh"] = new()
        {
            ["app.name"] = "Sofia",
            ["gate.title"] = "正在连接 Sofia",
            ["gate.probing"] = "正在查找本地服务器…",
            ["gate.ready"] = "已连接",
            ["gate.failed"] = "没有服务器响应",
            ["gate.url"] = "服务器地址",
            ["gate.token"] = "访问令牌",
            ["gate.pair"] = "配对",
            ["gate.code"] = "配对码",
            ["gate.retry"] = "重试",
            ["gate.log"] = "连接日志",
            ["shell.newChat"] = "新对话",
            ["shell.search"] = "搜索对话",
            ["shell.draft"] = "Sofia 应该做什么？",
            ["shell.send"] = "发送",
            ["shell.settings"] = "设置",
            ["shell.changes"] = "更改",
            ["shell.tasks"] = "任务",
            ["shell.emptyTitle"] = "开始一段对话",
            ["shell.emptyBody"] = "在左侧选择一个工作区并描述任务。",
            ["shell.working"] = "工作中",
        },
        ["ja"] = new()
        {
            ["app.name"] = "Sofia",
            ["gate.title"] = "Sofia に接続中",
            ["gate.probing"] = "ローカルサーバーを探しています…",
            ["gate.ready"] = "接続済み",
            ["gate.failed"] = "サーバーが応答しません",
            ["gate.url"] = "サーバー URL",
            ["gate.token"] = "アクセストークン",
            ["gate.pair"] = "ペアリング",
            ["gate.code"] = "ペアリングコード",
            ["gate.retry"] = "再試行",
            ["gate.log"] = "接続ログ",
            ["shell.newChat"] = "新しい会話",
            ["shell.search"] = "会話を検索",
            ["shell.draft"] = "Sofia に何をさせますか？",
            ["shell.send"] = "送信",
            ["shell.settings"] = "設定",
            ["shell.changes"] = "変更",
            ["shell.tasks"] = "タスク",
            ["shell.emptyTitle"] = "会話を始めましょう",
            ["shell.emptyBody"] = "左のワークスペースを選び、タスクを書いてください。",
            ["shell.working"] = "作業中",
        },
    };

    private static string _current = "en";

    public static event Action? Changed;

    public static string Current => _current;

    public static void SetLocale(string? code)
    {
        var next = code is not null && Strings.ContainsKey(code) ? code : "en";
        if (next == _current) return;
        _current = next;
        Changed?.Invoke();
    }

    /// <summary>Translates a key, falling back to English and then to the key itself.</summary>
    public static string T(string key)
    {
        if (Strings[_current].TryGetValue(key, out var value)) return value;
        if (Strings["en"].TryGetValue(key, out var english)) return english;
        return key;
    }

    /// <summary>Every key the app knows (used by the settings translation coverage check in 043).</summary>
    public static IReadOnlyCollection<string> Keys => Strings["en"].Keys;
}
