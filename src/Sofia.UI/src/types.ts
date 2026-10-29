// ---------------------------------------------------------------------------
// Shared types for Sofia.UI: the Sofia.Server wire DTOs (see
// docs/server_api.md) plus the view models and static option lists the
// components render.
// ---------------------------------------------------------------------------

// --- Sofia.Server DTOs -------------------------------------------------------

export type SessionStatus = 'idle' | 'working' | 'waiting' | 'permission' | 'question' | 'error';
export type WorkspaceKind = 'folder' | 'chat';

export interface UsageDto
{
  input: number;
  output: number;
  reasoning: number;
  cacheRead: number;
  cacheWrite: number;
  cost: number;
}

export interface ContextDto
{
  used: number;
  limit: number;
}

export interface ConversationSummaryDto
{
  id: string;
  workspaceId: string;
  title: string;
  createdAt: string;
  updatedAt: string;
  status: SessionStatus;
  parentId?: string;
  /** Parked in the workspace's "Archived" folder; hidden from the normal list. */
  archived?: boolean;
  archivedAt?: string;
  /** Kept at the top of the workspace's list. */
  pinned?: boolean;
  /** How long the last request of this chat took (ms), waiting on the user's answers excluded; persisted. */
  lastTurnMs?: number;
  /** When that request finished (ISO). */
  lastTurnAt?: string;
}

export interface WorkspaceDto
{
  id: string;
  name: string;
  path: string;
  kind: WorkspaceKind;
  icon: string;
  modelId: string;
  expanded: boolean;
  order: number;
  conversations?: ConversationSummaryDto[];
}

export interface AttachmentDto
{
  id: string;
  name: string;
  kind: AttachmentKind;
  mime: string;
  size: string;
  bytes: number;
  url: string;
}

export interface ToolCallDto
{
  id: string;
  name: string;
  status: ToolCallStatus;
  summary: string;
  input?: string;
  output?: string;
  error?: string;
  duration: string;
  childSessionId?: string;
  /** Media the tool produced (screenshots, images, audio or video). */
  images?: AttachmentDto[];
}

export interface MessageDto
{
  id: string;
  sessionId: string;
  role: 'user' | 'assistant';
  text: string;
  createdAt: string;
  model?: string;
  reasoning?: string;
  reasoningMode?: ReasoningModeId;
  /** Context updates (one bullet line each) the server attached to a user message. */
  contextNotes?: string[];
  attachments?: AttachmentDto[];
  toolCalls?: ToolCallDto[];
  permission?: {
    tool: string;
    description: string;
    choices: string[];
    resolved?: string;
    requestId: string;
  };
  question?: {
    prompt: string;
    options: string[];
    resolved?: string;
    formId: string;
    fieldKey: string;
    multi: boolean;
  };
  questionSet?: {
    questions: { prompt: string; options: string[]; fieldKey: string; multi: boolean; }[];
    answers?: ( string | null )[];
    confirmed?: boolean;
    formId: string;
  };
  error?: string;
  summary?: boolean;
  /** System notice for a context compaction (progress and result). */
  compaction?: CompactionNoticeDto;
  /** Transient provider failure Sofia is repeating; cleared by the attempt that succeeds. */
  retry?: RetryNoticeDto;
  usage?: UsageDto;
  durationMs?: number;
  /** Notes the harness learned from the conversation (card-only message). */
  knowledge?: KnowledgeEntryDto[];
}

export interface CompactionNoticeDto
{
  status: 'running' | 'done' | 'failed' | 'skipped';
  auto: boolean;
  /** Estimated context tokens before compaction. */
  before: number;
  /** Estimated context tokens after compaction (done only). */
  after?: number;
  error?: string;
  /** The generated summary (done only); shown when the card is expanded. */
  result?: string;
}

/** A transient provider failure Sofia is repeating: shown while the next attempt runs (attempt/attempts are 1-based). */
export interface RetryNoticeDto
{
  attempt: number;
  attempts: number;
  /** Why the previous attempt failed, one line. */
  error?: string;
}

export interface KnowledgeEntryDto
{
  id: string;
  scope: 'workspace' | 'agent';
  text: string;
  /** Why the reviewer judged the note worth keeping permanently (the utility argument). */
  why?: string;
  /** The user removed the note after it was announced. */
  deleted?: boolean;
}

export interface SessionDto extends ConversationSummaryDto
{
  modelId: string;
  agent: string;
  permissionMode: PermissionModeId;
  reasoningMode: ReasoningModeId;
  usage: UsageDto;
  context: ContextDto;
  activeSkills: string[];
  queued: number;
  /** Prompts waiting to reach the model, steer first (preview under the transcript). */
  queuedMessages?: QueuedMessageDto[];
  messages?: MessageDto[];
}

export interface QueuedMessageDto
{
  id: string;
  text: string;
  delivery: 'steer' | 'queue';
  attachments?: AttachmentDto[];
}

export interface MetricsDto
{
  /** Clock of the request in flight (ms); 0 between runs. */
  durationMs: number;
  /** What the last finished request took (ms), user-waiting time excluded. */
  lastTurnMs?: number;
  lastTurnAt?: string;
  tokensPerSec: number;
  usage: UsageDto;
  cost: number;
  costAvailable: boolean;
  context: ContextDto;
}

export interface FileDiffDto
{
  file: string;
  patch: string;
  additions: number;
  deletions: number;
  status: 'added' | 'deleted' | 'modified';
}

export interface ConfigDto
{
  defaultModel: string;
  shell: string;
  autoCompact: boolean;
  compactThreshold: number;
  discovery: boolean;
  maxToolOutput: number;
  keepScreenAwake: boolean;
  knowledge: boolean;
  searchProvider: 'duckduckgo' | 'searxng';
  searxngUrl: string;
  dataDir: string;
  configPath: string;
}

export interface ProviderPresetDto
{
  id: string;
  name: string;
  type: 'openai' | 'openai-responses' | 'anthropic';
  baseUrl: string;
  envKey?: string;
  /** 'github-device' = sign in through the GitHub device flow instead of pasting a key. */
  auth?: 'github-device';
  note?: string;
}

export interface CopilotDeviceFlow
{
  deviceCode: string;
  userCode: string;
  verificationUri: string;
  interval: number;
  expiresIn: number;
}

export interface CopilotLoginPoll
{
  status: 'pending' | 'success' | 'expired' | 'denied';
  interval?: number | null;
  provider?: Provider | null;
}

export interface AgentDto
{
  id: string;
  name: string;
  description: string;
  /** 'primary' agents are picked in the agent dropdown, 'subagent' ones are called with @name, 'all' are both. */
  mode: 'primary' | 'subagent' | 'all';
  source?: 'builtin' | 'global' | 'project';
  path?: string | null;
}

export interface CommandDto
{
  name: string;
  description: string;
  source: 'builtin' | 'global' | 'project';
  agent?: string | null;
  model?: string | null;
  subtask?: boolean;
  path?: string | null;
}

export interface SkillDto
{
  id: string;
  name: string;
  description: string;
  path: string;
  content: string;
  source: string;
}

export interface StatsDto
{
  sessions: number;
  prompts: number;
  steps: number;
  tokens: { input: number; output: number; };
  cost: number;
  activeDays: number;
  streak: number;
}

/** One live event pushed by the `/hubs/events` hub (`resync` is local-only). */
export interface SofiaEvent
{
  type: string;
  sessionId?: string;
  workspaceId?: string;
  data: unknown;
}

/** Payload of `config.changed`: command / skill / agent / instruction files were edited on disk. */
export interface ConfigChangedDto
{
  /** `global` is the harness home (`~/.sofia`), `project` is one workspace folder. */
  scope: 'global' | 'project';
  /** Harness home folder for `global`, workspace folder for `project`. */
  directory: string;
}

// --- View models -------------------------------------------------------------

export type AttachmentKind = 'image' | 'video' | 'audio' | 'file';

export interface Attachment
{
  id: string;
  name: string;
  kind: AttachmentKind;
  /** Human readable size, e.g. "1.2 MB". */
  size: string;
  mime?: string;
  /** Server URL (`/api/attachments/{id}`), relative to the server. */
  url?: string;
  /** Local object URL for previews of files not uploaded yet. */
  previewUrl?: string;
}

export type ToolCallStatus = 'success' | 'error' | 'running';

export interface ToolCall
{
  id: string;
  name: string;
  status: ToolCallStatus;
  /** One-line summary of the call (usually its main argument). */
  summary: string;
  input?: string;
  output?: string;
  error?: string;
  duration: string;
  /** Subagent session spawned by this call (task tool). */
  childSessionId?: string;
  /** Media the tool produced (screenshots, images, audio or video). */
  images?: Attachment[];
}

export interface Message
{
  /** Server message id (local-only error cards use a `local-` prefix). */
  id: string;
  sessionId: string;
  role: 'user' | 'assistant';
  text: string;
  createdAt: string;
  /** Clock label derived from `createdAt`. */
  time: string;
  /** Context updates the server attached to this user message. */
  contextNotes?: string[];
  attachments?: Attachment[];
  model?: string;
  reasoning?: string;
  reasoningMode?: ReasoningModeId;
  toolCalls?: ToolCall[];
  permission?: {
    tool: string;
    description: string;
    choices: string[];
    resolved?: string;
    requestId: string;
  };
  question?: {
    prompt: string;
    options: string[];
    resolved?: string;
    formId: string;
    fieldKey: string;
    multi: boolean;
  };
  questionSet?: {
    questions: { prompt: string; options: string[]; fieldKey: string; multi: boolean; }[];
    answers?: ( string | null )[];
    confirmed?: boolean;
    formId: string;
  };
  error?: string;
  /** Compaction summary card. */
  summary?: boolean;
  /** System notice for a context compaction (progress and result). */
  compaction?: CompactionNoticeDto;
  /** Transient provider failure Sofia is repeating; cleared by the attempt that succeeds. */
  retry?: RetryNoticeDto;
  usage?: UsageDto;
  durationMs?: number;
  /** Notes the harness learned from the conversation (card-only message). */
  knowledge?: KnowledgeEntryDto[];
  /** A structured report card (deep-search style output). */
  report?: ReportBlock;
  /** Client-side card (e.g. a failed request) that the server never saw. */
  local?: boolean;
}

/**
 * An assistant step whose prompt was almost fully re-billed: under the low-cache
 * threshold of its tokens came from the provider's prompt cache. `reasons` are
 * i18n keys of suspects read off the transcript (never a provider claim).
 */
export interface CacheBreakInfo
{
  /** Cache hit percentage of that step's prompt. */
  pct: number;
  cached: number;
  input: number;
  /** `cache.reason*` keys, most likely first. */
  reasons: string[];
}

/** A structured report produced by a deep-search run. */
export interface ReportBlock
{
  title: string;
  summary: string;
  sections: { heading: string; body: string; }[];
  file?: { name: string; size: string; };
}

export interface Conversation
{
  /** Server session id. */
  id: string;
  workspaceId: string;
  title: string;
  /** Relative label ("5m ago"). */
  updatedAt: string;
  updatedAtIso: string;
  createdAt: string;
  status: WorkspaceStatus;
  /** Mirrors `status === 'working'` (drives the sidebar animation). */
  working: boolean;
  /** Set for subagent sessions (hidden from the sidebar). */
  parentId?: string;
  /** Parked in the workspace's "Archived" folder (hidden from the normal list). */
  archived?: boolean;
  /** Kept at the top of the workspace's list. */
  pinned?: boolean;
  /** Whether `messages` was fetched from the server. */
  loaded: boolean;
  messages: Message[];
  modelId?: string;
  agent?: string;
  permissionMode?: PermissionModeId;
  reasoningMode?: ReasoningModeId;
  usage?: UsageDto;
  context?: ContextDto;
  /** What the last request of this chat took (server-side, user-waiting time excluded). */
  lastTurnMs?: number;
  activeSkills?: string[];
  queued?: number;
  /** Follow-ups accepted by the server but not yet sent to the model. */
  queuedMessages?: QueuedMessageDto[];
}

export interface Workspace
{
  id: string;
  name: string;
  /** Absolute folder path. Empty for chat groups. */
  path: string;
  kind: WorkspaceKind;
  /** Icon key (see WORKSPACE_ICON_CHOICES). */
  icon: string;
  /** Default model for this workspace's conversations ('' = server default). */
  modelId: string;
  expanded: boolean;
  order: number;
  conversations: Conversation[];
}

/** LLM activity shown as a badge / animated border on sidebar rows. */
export type WorkspaceStatus = 'idle' | 'working' | 'waiting' | 'done' | 'permission' | 'question' | 'error';

/** A model from `GET /api/models`. */
export interface ModelOption
{
  id: string;
  providerId: string;
  modelId: string;
  label: string;
  provider: string;
  local: boolean;
  contextLength: number;
  maxOutput: number;
  context: string;
  connected: boolean;
  reasoning: boolean;
  vision: boolean;
  audio: boolean;
  video: boolean;
  loaded?: boolean;
  enabled: boolean;
  /** Serves one conversation at a time: runs queue (subagents first) and a conversation keeps the model until it finishes. Local models are single client by default. */
  singleClient?: boolean;
}

export type PermissionModeId = 'read-only' | 'ask' | 'plan' | 'full';
export interface PermissionMode
{
  id: PermissionModeId;
  label: string;
  description: string;
}

export type ReasoningModeId = 'off' | 'low' | 'medium' | 'high';
export interface ReasoningMode
{
  id: ReasoningModeId;
  label: string;
  description: string;
}

/** An MCP server from `GET /api/mcp`. */
export interface McpServer
{
  id: string;
  name: string;
  transport: 'command' | 'web';
  command: string;
  url: string;
  hasToken: boolean;
  enabled: boolean;
  status: 'connected' | 'error' | 'stopped' | 'pending';
  error?: string;
  tools: number;
  toolNames: string[];
}

/** A model provider from `GET /api/providers`. */
export interface Provider
{
  id: string;
  name: string;
  kind: 'local' | 'cloud';
  type: 'openai' | 'openai-responses' | 'anthropic';
  baseUrl: string;
  status: 'connected' | 'disconnected' | 'error';
  detail: string;
  enabled: boolean;
  custom: boolean;
  discovered: boolean;
  hasKey: boolean;
  models: number;
  /** Earlier reasoning is resent with the history (effective value). */
  preserveThinking: boolean;
}

/** The signed-in user's profile (editable in settings). */
export interface UserProfile
{
  name: string;
  handle: string;
  /** Emoji used as the avatar (fallback when no photo is set). */
  avatar: string;
  /** Optional uploaded photo (object URL) shown instead of the emoji. */
  avatarImage?: string;
}

/** A selectable UI language. */
export interface Language
{
  code: string;
  label: string;
}

/** One line in a side-by-side compare view. */
export interface DiffLine
{
  oldNo?: number;
  newNo?: number;
  type: 'ctx' | 'add' | 'del';
  text: string;
}

/** A changed file shown in the right-side workspace panel. */
export interface FileChange
{
  path: string;
  status: 'M' | 'A' | 'D';
  additions?: number;
  deletions?: number;
  diff?: DiffLine[];
}

// --- App constants -----------------------------------------------------------

export const APP_NAME = 'Sofia Harness';
export const APP_VERSION = '0.1.0';

export const PERMISSION_MODES: PermissionMode[] = [
  { id: 'read-only', label: 'Read only', description: 'Can read files but never write or run commands.' },
  { id: 'ask', label: 'Ask before edits', description: 'Prompts before any write, edit or command.' },
  { id: 'plan', label: 'Plan', description: 'Researches and proposes a plan without applying changes.' },
  { id: 'full', label: 'Full access', description: 'Edits files and runs commands without asking.' },
];

export const REASONING_MODES: ReasoningMode[] = [
  { id: 'off', label: 'Off', description: 'Answer directly, no extended thinking.' },
  { id: 'low', label: 'Low', description: 'Brief internal reasoning.' },
  { id: 'medium', label: 'Medium', description: 'Balanced reasoning depth.' },
  { id: 'high', label: 'High', description: 'Extended reasoning for hard problems.' },
];

/** Icons a workspace can be assigned (keys into SofiaIcon). */
export const WORKSPACE_ICON_CHOICES: string[] = [
  'folder', 'code', 'book', 'palette', 'flask', 'briefcase',
  'globe', 'terminal', 'database', 'server', 'star', 'music',
  'camera', 'layers', 'cpu', 'box',
  // Chat-group friendly marks (no folder implied).
  'chat', 'sparkles', 'bell', 'help', 'languages', 'user',
  'search', 'clock',
];

/** UI languages. */
export const LANGUAGES: Language[] = [
  { code: 'en', label: 'English' },
  { code: 'pt', label: 'Português' },
  { code: 'es', label: 'Español' },
  { code: 'fr', label: 'Français' },
  { code: 'zh', label: '简体中文' },
  { code: 'ja', label: '日本語' },
];

// --- Permission choice mapping ------------------------------------------------

/** Choices on permission cards (the server always sends these three). */
export const LIVE_PERMISSION_CHOICES = [ 'Allow once', 'Allow for this session', 'Deny' ];

/** Maps a card choice to the server decision. */
export function permissionDecision ( choice: string ): 'once' | 'always' | 'reject'
{
  const normalized = choice.toLowerCase();
  if ( normalized.includes( 'session' ) ) return 'always';
  if ( normalized.startsWith( 'deny' ) ) return 'reject';
  return 'once';
}

/** Maps a server decision back to the card choice label. */
export function permissionChoiceForReply ( reply: string ): string
{
  if ( reply === 'always' ) return 'Allow for this session';
  if ( reply === 'reject' ) return 'Deny';
  return 'Allow once';
}
