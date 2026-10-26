import { computed, reactive, ref, watch, watchEffect } from 'vue';

import { useSound } from '@/composables/useSound';
import { useSettings } from '@/composables/useSettings';
import { api, onServerEvent, serverUrl, uploadAttachment } from '@/services/sofia';
import
  {
    LIVE_PERMISSION_CHOICES,
    permissionDecision,
    type Attachment,
    type AttachmentDto,
    type AttachmentKind,
    type Conversation,
    type ConversationSummaryDto,
    type Message,
    type MessageDto,
    type MetricsDto,
    type ModelOption,
    type PermissionModeId,
    type ReasoningModeId,
    type SessionDto,
    type SessionStatus,
    type SofiaEvent,
    type Workspace,
    type WorkspaceDto,
    type WorkspaceStatus,
  } from '@/types';

// ---------------------------------------------------------------------------
// Central harness store, backed by Sofia.Server. A module-level singleton so
// any component can call `useHarness()` and share the same reactive state.
//
// The workspace tree, transcripts, models and run state come from the REST
// API and stay live through the SignalR event stream (one global
// subscription below). Only the UI selection and the two composer modes are
// persisted locally (sessionStorage `sofia.harness.v2`).
// ---------------------------------------------------------------------------

const STORAGE_KEY = 'sofia.harness.v2';
const BUSY: ReadonlySet<WorkspaceStatus> = new Set<WorkspaceStatus>( [ 'working', 'permission', 'question' ] );

/**
 * A composer attachment. Fresh picks keep the File for the upload; ones
 * restored from a reverted message are already on the server (`uploadedId`).
 */
export interface PendingAttachment extends Attachment
{
  file?: File;
  uploadedId?: string;
}

interface HarnessState
{
  workspaces: Workspace[];
  activeWorkspaceId: string;
  activeConversationId: string;
  permissionMode: PermissionModeId;
  reasoningMode: ReasoningModeId;
  composerText: string;
  attachments: PendingAttachment[];
  /** The active conversation is running (working / permission / question). */
  isReplying: boolean;
  /** The busy active conversation, if any. */
  replyingConversationId: string | null;
  loading: boolean;
  /** Last store-level failure (workspace list, mutations). */
  error: string | null;
}

// --- helpers ------------------------------------------------------------------

function uid ( prefix: string ): string
{
  return `${ prefix }-${ Math.random().toString( 36 ).slice( 2, 9 ) }`;
}

function errorText ( e: unknown ): string
{
  return e instanceof Error ? e.message : String( e );
}

function formatBytes ( bytes: number ): string
{
  if ( bytes < 1024 ) return `${ bytes } B`;
  const units = [ 'KB', 'MB', 'GB' ];
  let value = bytes / 1024;
  let unit = 0;
  while ( value >= 1024 && unit < units.length - 1 )
  {
    value /= 1024;
    unit += 1;
  }
  return `${ value.toFixed( 1 ) } ${ units[ unit ] }`;
}

function kindFromMime ( mime: string ): AttachmentKind
{
  if ( mime.startsWith( 'image/' ) ) return 'image';
  if ( mime.startsWith( 'video/' ) ) return 'video';
  if ( mime.startsWith( 'audio/' ) ) return 'audio';
  return 'file';
}

/** Short relative label for a timestamp ("just now", "5m ago", "yesterday"…). */
export function relativeTime ( iso: string ): string
{
  const time = Date.parse( iso );
  if ( Number.isNaN( time ) ) return '';
  const minute = 60_000;
  const diff = Date.now() - time;
  if ( diff < minute ) return 'just now';
  if ( diff < 60 * minute ) return `${ Math.floor( diff / minute ) }m ago`;
  if ( diff < 24 * 60 * minute ) return `${ Math.floor( diff / ( 60 * minute ) ) }h ago`;
  if ( diff < 48 * 60 * minute ) return 'yesterday';
  return new Date( time ).toLocaleDateString();
}

function clockTime ( iso: string ): string
{
  const time = Date.parse( iso );
  const date = Number.isNaN( time ) ? new Date() : new Date( time );
  return date.toLocaleTimeString( [], { hour: '2-digit', minute: '2-digit' } );
}

function samePath ( a: string, b: string ): boolean
{
  const norm = ( p: string ): string => p.replace( /\//g, '\\' ).replace( /\\+$/, '' ).toLowerCase();
  return norm( a ) === norm( b );
}

// --- DTO mapping --------------------------------------------------------------

function mapAttachment ( a: AttachmentDto ): Attachment
{
  return { id: a.id, name: a.name, kind: a.kind, size: a.size, mime: a.mime, url: a.url };
}

function mapMessage ( m: MessageDto ): Message
{
  return {
    id: m.id,
    sessionId: m.sessionId,
    role: m.role,
    text: m.text ?? '',
    createdAt: m.createdAt,
    time: clockTime( m.createdAt ),
    model: m.model,
    reasoning: m.reasoning,
    reasoningMode: m.reasoningMode,
    attachments: m.attachments?.map( mapAttachment ),
    toolCalls: m.toolCalls?.map( ( t ) => ( { ...t, images: t.images?.map( mapAttachment ) } ) ),
    permission: m.permission
      ? {
        ...m.permission,
        choices: m.permission.choices?.length ? [ ...m.permission.choices ] : [ ...LIVE_PERMISSION_CHOICES ],
      }
      : undefined,
    question: m.question ? { ...m.question, options: [ ...( m.question.options ?? [] ) ] } : undefined,
    questionSet: m.questionSet
      ? {
        ...m.questionSet,
        questions: m.questionSet.questions.map( ( q ) => ( { ...q, options: [ ...( q.options ?? [] ) ] } ) ),
        answers: [ ...( m.questionSet.answers ?? [] ) ],
      }
      : undefined,
    error: m.error,
    summary: m.summary,
    knowledge: m.knowledge?.map( ( k ) => ( { ...k } ) ),
    compaction: m.compaction ? { ...m.compaction } : undefined,
    usage: m.usage,
    durationMs: m.durationMs,
  };
}

/** Replaces `target`'s fields with `source`'s, keeping the reactive object. */
function replaceInto<T extends object> ( target: T, source: T ): void
{
  const t = target as Record<string, unknown>;
  const s = source as Record<string, unknown>;
  for ( const key of Object.keys( t ) ) if ( !( key in s ) ) delete t[ key ];
  Object.assign( t, s );
}

function newConversation ( dto: ConversationSummaryDto ): Conversation
{
  return {
    id: dto.id,
    workspaceId: dto.workspaceId,
    title: dto.title || 'New conversation',
    createdAt: dto.createdAt,
    updatedAtIso: dto.updatedAt,
    updatedAt: relativeTime( dto.updatedAt ),
    status: dto.status ?? 'idle',
    working: dto.status === 'working',
    parentId: dto.parentId || undefined,
    archived: !!dto.archived,
    pinned: !!dto.pinned,
    loaded: false,
    messages: [],
  };
}

/** Copies session-level details (model, modes, usage…) onto a conversation. */
function applySessionDetails ( conv: Conversation, s: SessionDto ): void
{
  conv.title = s.title || conv.title;
  conv.createdAt = s.createdAt ?? conv.createdAt;
  if ( s.updatedAt )
  {
    conv.updatedAtIso = s.updatedAt;
    conv.updatedAt = relativeTime( s.updatedAt );
  }
  conv.parentId = s.parentId || undefined;
  if ( s.archived !== undefined ) conv.archived = !!s.archived;
  if ( s.pinned !== undefined ) setPinnedLocal( conv, !!s.pinned );
  conv.modelId = s.modelId;
  conv.agent = s.agent;
  conv.permissionMode = s.permissionMode;
  conv.reasoningMode = s.reasoningMode;
  conv.usage = s.usage;
  conv.context = s.context;
  conv.activeSkills = [ ...( s.activeSkills ?? [] ) ];
  conv.queued = s.queued;
  conv.queuedMessages = s.queuedMessages ?? [];
}

// --- persistence (UI selection only) ------------------------------------------

interface PersistedShape
{
  activeWorkspaceId: string;
  activeConversationId: string;
  permissionMode: PermissionModeId;
  reasoningMode: ReasoningModeId;
}

function loadPersisted (): Partial<PersistedShape>
{
  try
  {
    const raw = sessionStorage.getItem( STORAGE_KEY );
    return raw ? ( JSON.parse( raw ) as Partial<PersistedShape> ) : {};
  } catch
  {
    return {};
  }
}

const persisted = loadPersisted();

const state = reactive<HarnessState>( {
  workspaces: [],
  activeWorkspaceId: persisted.activeWorkspaceId ?? '',
  activeConversationId: persisted.activeConversationId ?? '',
  permissionMode: persisted.permissionMode ?? 'ask',
  reasoningMode: persisted.reasoningMode ?? 'medium',
  composerText: '',
  attachments: [],
  isReplying: false,
  replyingConversationId: null,
  loading: false,
  error: null,
} );

watch(
  () => ( {
    activeWorkspaceId: state.activeWorkspaceId,
    activeConversationId: state.activeConversationId,
    permissionMode: state.permissionMode,
    reasoningMode: state.reasoningMode,
  } ),
  ( snapshot: PersistedShape ) =>
  {
    try
    {
      sessionStorage.setItem( STORAGE_KEY, JSON.stringify( snapshot ) );
    } catch
    {
      /* storage unavailable */
    }
  },
);

// --- per-conversation composer drafts and modes ---------------------------------

/** Unsent prompt (text + attachments) of every conversation other than the open one. */
const drafts = new Map<string, { text: string; attachments: PendingAttachment[]; }>();

// Switching chats parks the composer content with the chat it belongs to and
// loads the other chat's own draft; permission/reasoning follow the chat too.
watch(
  () => state.activeConversationId,
  ( id, previous ) =>
  {
    if ( previous && ( state.composerText || state.attachments.length ) )
    {
      drafts.set( previous, { text: state.composerText, attachments: state.attachments } );
    }
    const draft = id ? drafts.get( id ) : undefined;
    if ( id ) drafts.delete( id );
    state.composerText = draft?.text ?? '';
    state.attachments = draft?.attachments ?? [];
    const conv = findConversation( id );
    if ( conv?.permissionMode ) state.permissionMode = conv.permissionMode;
    if ( conv?.reasoningMode ) state.reasoningMode = conv.reasoningMode;
  },
  { flush: 'sync' },
);

/** Gives a failed prompt back to its chat: the composer when open, else its parked draft. */
function restoreDraft ( conv: Conversation, text: string, attachments: PendingAttachment[] ): void
{
  if ( conv.id === state.activeConversationId )
  {
    if ( !state.composerText ) state.composerText = text;
    if ( !state.attachments.length ) state.attachments = attachments;
    return;
  }
  const draft = drafts.get( conv.id );
  if ( !draft?.text ) drafts.set( conv.id, { text, attachments: draft?.attachments.length ? draft.attachments : attachments } );
}

/** Bumped whenever a tool call finishes; the workspace panel refreshes its file list on it. */
const toolActivity = reactive( { tick: 0, workspaceId: '' } );

/** Live run readouts for the active conversation (composer hint row). */
const metrics = reactive( {
  durationMs: 0,
  tokensPerSec: 0,
  sessionCost: 0,
  costAvailable: false,
  contextUsed: 0,
  contextLimit: 0,
} );

/** Model catalog from `GET /api/models`. */
const models = ref<ModelOption[]>( [] );

const { playSound } = useSound();
const { settings } = useSettings();

/** Pending debounced workspace renames, by workspace id. */
const renameTimers = new Map<string, number>();
/** Guards against a double submit while a prompt is being posted. */
let sendingPrompt = false;

// --- lookups ------------------------------------------------------------------

function findWorkspace ( id: string ): Workspace | undefined
{
  return state.workspaces.find( ( w ) => w.id === id );
}

function findConversation ( id: string | undefined ): Conversation | undefined
{
  if ( !id ) return undefined;
  for ( const ws of state.workspaces )
  {
    const conv = ws.conversations.find( ( c ) => c.id === id );
    if ( conv ) return conv;
  }
  return undefined;
}

function firstVisible ( ws: Workspace ): Conversation | undefined
{
  return ws.conversations.find( ( c ) => !c.parentId && !c.archived );
}

function activeConv (): Conversation | undefined
{
  return findConversation( state.activeConversationId );
}

/** First enabled, connected model: the default for new workspaces. */
function defaultModelId (): string
{
  return models.value.find( ( m ) => m.connected && m.enabled !== false )?.id ?? '';
}

/** The model a conversation runs on: its own, else its workspace's, else the default. */
function modelOf ( conv: Conversation | undefined ): string
{
  return conv?.modelId || findWorkspace( conv?.workspaceId ?? '' )?.modelId || defaultModelId();
}

function sortWorkspaces (): void
{
  state.workspaces.sort( ( a, b ) => a.order - b.order );
}

// --- run state ----------------------------------------------------------------

watchEffect( () =>
{
  const conv = activeConv();
  const busy = !!conv && BUSY.has( conv.status );
  state.isReplying = busy;
  state.replyingConversationId = busy && conv ? conv.id : null;
} );

function setStatusLocal ( conv: Conversation, status: WorkspaceStatus ): void
{
  conv.status = status;
  conv.working = status === 'working';
}

/** Desktop notification when a session needs attention and the window is not focused. */
function notifyDesktop ( conv: Conversation, kind: 'permission' | 'question' ): void
{
  if ( typeof Notification === 'undefined' || document.hasFocus() ) return;
  const show = (): void =>
  {
    const n = new Notification( kind === 'permission' ? 'Sofia: permissão necessária' : 'Sofia: pergunta pendente', {
      body: conv.title || 'Uma sessão aguarda sua resposta',
      tag: `sofia-${ conv.id }`
    } );
    n.onclick = (): void => { window.focus(); n.close(); };
  };
  if ( Notification.permission === 'granted' ) show();
  else if ( Notification.permission === 'default' ) void Notification.requestPermission().then( ( p ) => { if ( p === 'granted' ) show(); } );
}

/**
 * Applies a server status. A run that ends while its conversation is not
 * open shows a 'done' badge until the user opens it. Sounds play for the
 * active conversation only.
 */
function applyStatus ( conv: Conversation, status: SessionStatus, quiet = false ): void
{
  const prev = conv.status;
  const isActive = conv.id === state.activeConversationId;
  let next: WorkspaceStatus = status;
  if ( status === 'idle' && ( BUSY.has( prev ) || prev === 'done' ) && !isActive ) next = 'done';
  setStatusLocal( conv, next );
  if ( !quiet && prev !== next && ( next === 'permission' || next === 'question' ) ) notifyDesktop( conv, next );
  if ( quiet || !isActive || prev === next ) return;
  if ( next === 'error' ) playSound( 'error' );
  else if ( next === 'permission' || next === 'question' ) playSound( 'permission' );
  else if ( next === 'idle' && BUSY.has( prev ) ) playSound( 'done' );
}

function resetMetricsFor ( conv: Conversation | undefined ): void
{
  metrics.durationMs = 0;
  metrics.tokensPerSec = 0;
  metrics.sessionCost = conv?.usage?.cost ?? 0;
  metrics.costAvailable = ( conv?.usage?.cost ?? 0 ) > 0;
  metrics.contextUsed = conv?.context?.used ?? 0;
  metrics.contextLimit = conv?.context?.limit ?? 0;
}

function applyMetrics ( data: MetricsDto ): void
{
  metrics.durationMs = data.durationMs ?? 0;
  metrics.tokensPerSec = Math.round( data.tokensPerSec ?? 0 );
  metrics.sessionCost = data.cost ?? data.usage?.cost ?? 0;
  metrics.costAvailable = !!data.costAvailable;
  metrics.contextUsed = data.context?.used ?? 0;
  metrics.contextLimit = data.context?.limit ?? 0;
}

function pushLocalError ( conv: Conversation, text: string ): void
{
  const now = new Date().toISOString();
  conv.messages.push( {
    id: uid( 'local' ),
    sessionId: conv.id,
    role: 'assistant',
    text: '',
    error: text,
    createdAt: now,
    time: clockTime( now ),
    local: true,
  } );
}

/** Applies a pin change and moves the row to the front of its group (the list shows pinned rows first). */
function setPinnedLocal ( conv: Conversation, pinned: boolean ): void
{
  if ( !!conv.pinned === pinned ) return;
  conv.pinned = pinned;
  const list = findWorkspace( conv.workspaceId )?.conversations;
  const index = list?.findIndex( ( c ) => c.id === conv.id ) ?? -1;
  if ( list && index > 0 )
  {
    list.splice( index, 1 );
    list.unshift( conv );
  }
}

// --- tree sync ----------------------------------------------------------------

/** Inserts or updates a conversation from a summary, keeping loaded state. */
function upsertConversation ( ws: Workspace, dto: ConversationSummaryDto, quiet = true ): Conversation
{
  const existing = findConversation( dto.id );
  if ( existing )
  {
    existing.workspaceId = dto.workspaceId ?? existing.workspaceId;
    existing.title = dto.title || existing.title;
    existing.createdAt = dto.createdAt ?? existing.createdAt;
    if ( dto.updatedAt )
    {
      existing.updatedAtIso = dto.updatedAt;
      existing.updatedAt = relativeTime( dto.updatedAt );
    }
    existing.parentId = dto.parentId || undefined;
    if ( dto.archived !== undefined ) existing.archived = !!dto.archived;
    if ( dto.pinned !== undefined ) existing.pinned = !!dto.pinned;
    if ( dto.status ) applyStatus( existing, dto.status, quiet );
    return existing;
  }
  ws.conversations.unshift( newConversation( dto ) );
  return ws.conversations[ 0 ];
}

/** Inserts or updates a workspace; `conversations` (when present) replaces the list. */
function upsertWorkspace ( dto: WorkspaceDto ): Workspace
{
  const fields = {
    name: dto.name,
    path: dto.path ?? '',
    kind: dto.kind === 'chat' ? ( 'chat' as const ) : ( 'folder' as const ),
    icon: dto.icon || ( dto.kind === 'chat' ? 'chat' : 'folder' ),
    modelId: dto.modelId ?? '',
    expanded: !!dto.expanded,
    order: dto.order ?? 0,
  };
  let ws = findWorkspace( dto.id );
  if ( ws )
  {
    Object.assign( ws, fields );
  } else
  {
    state.workspaces.push( { id: dto.id, ...fields, conversations: [] } );
    ws = state.workspaces[ state.workspaces.length - 1 ];
  }
  if ( dto.conversations )
  {
    ws.conversations = dto.conversations.map( ( c ) => findConversation( c.id ) ?? newConversation( c ) );
    for ( const c of dto.conversations ) upsertConversation( ws, c );
  }
  sortWorkspaces();
  return ws;
}

let creatingDefault = false;
let loadingWorkspaces: Promise<void> | null = null;

/** Fetches the whole tree; creates a "Quick chats" group on an empty server. */
function loadWorkspaces (): Promise<void>
{
  if ( loadingWorkspaces ) return loadingWorkspaces;
  loadingWorkspaces = ( async () =>
  {
    state.loading = true;
    try
    {
      let dtos = await api<WorkspaceDto[]>( 'GET', '/api/workspaces' );
      if ( !dtos.length && !creatingDefault )
      {
        creatingDefault = true;
        try
        {
          const created = await api<WorkspaceDto>( 'POST', '/api/workspaces', {
            kind: 'chat',
            name: 'Quick chats',
            icon: 'chat',
            modelId: defaultModelId() || undefined,
          } );
          dtos = [ created ];
        } finally
        {
          creatingDefault = false;
        }
      }
      const keep = new Set( dtos.map( ( d ) => d.id ) );
      state.workspaces = state.workspaces.filter( ( w ) => keep.has( w.id ) );
      for ( const dto of dtos ) upsertWorkspace( dto );
      state.error = null;
      await ensureSelection();
    } catch ( e )
    {
      state.error = errorText( e );
    } finally
    {
      state.loading = false;
      loadingWorkspaces = null;
    }
  } )();
  return loadingWorkspaces;
}

let refreshTimer: number | undefined;

/** Debounced full re-fetch, for events that reference unknown items. */
function scheduleRefresh (): void
{
  if ( refreshTimer !== undefined ) window.clearTimeout( refreshTimer );
  refreshTimer = window.setTimeout( () =>
  {
    refreshTimer = undefined;
    void loadWorkspaces();
  }, 300 );
}

async function refreshModels (): Promise<void>
{
  try
  {
    models.value = await api<ModelOption[]>( 'GET', '/api/models' );
  } catch
  {
    /* keep the last catalog */
  }
}

const loadingConversations = new Map<string, Promise<void>>();
/** Message events that arrived while a transcript was being fetched. */
const deferredEvents = new Map<string, SofiaEvent[]>();

/** Fetches a session with its messages (lazy transcript loading). */
function loadConversation ( conv: Conversation ): Promise<void>
{
  const pending = loadingConversations.get( conv.id );
  if ( pending ) return pending;
  deferredEvents.set( conv.id, [] );
  const task = ( async () =>
  {
    try
    {
      const s = await api<SessionDto>( 'GET', `/api/sessions/${ conv.id }` );
      applySessionDetails( conv, s );
      conv.messages = ( s.messages ?? [] ).map( mapMessage );
      conv.loaded = true;
      if ( s.status ) applyStatus( conv, s.status, true );
      if ( conv.id === state.activeConversationId )
      {
        if ( s.permissionMode ) state.permissionMode = s.permissionMode;
        if ( s.reasoningMode ) state.reasoningMode = s.reasoningMode;
        resetMetricsFor( conv );
      }
    } catch ( e )
    {
      state.error = errorText( e );
    } finally
    {
      loadingConversations.delete( conv.id );
      const missed = deferredEvents.get( conv.id ) ?? [];
      deferredEvents.delete( conv.id );
      // Replay created/updated/deleted (idempotent); deltas may already be in
      // the snapshot, and the final `message.updated` carries the full text.
      if ( conv.loaded ) for ( const event of missed ) handleEvent( event );
    }
  } )();
  loadingConversations.set( conv.id, task );
  return task;
}

/** Serialized selection work: a local removal and the matching server event must
 *  not each create a fresh conversation. */
let selectionChain: Promise<void> = Promise.resolve();

/** Keeps the selection valid; picks the first conversation when it is not. */
function ensureSelection (): Promise<void>
{
  const run = selectionChain.then( ensureSelectionInner, ensureSelectionInner );
  // Internally every step already reports its own failures, so the boundary
  // swallows them: callers `void` this from event handlers too.
  selectionChain = run.catch( () => undefined );
  return selectionChain;
}

async function ensureSelectionInner (): Promise<void>
{
  let conv = activeConv();
  // An archived conversation cannot stay open: the tree hides it from the list.
  if ( conv?.archived ) conv = undefined;
  let ws = conv ? findWorkspace( conv.workspaceId ) : undefined;
  if ( !conv || !ws )
  {
    ws = findWorkspace( state.activeWorkspaceId );
    conv = ws ? firstVisible( ws ) : undefined;
    if ( !conv )
    {
      ws = state.workspaces.find( ( w ) => firstVisible( w ) ) ?? ws ?? state.workspaces[ 0 ];
      conv = ws ? firstVisible( ws ) : undefined;
    }
  }
  if ( !ws ) return;
  state.activeWorkspaceId = ws.id;
  if ( !conv )
  {
    await createConversation( ws.id );
    return;
  }
  state.activeConversationId = conv.id;
  if ( conv.status === 'done' ) setStatusLocal( conv, 'idle' );
  if ( !conv.loaded ) await loadConversation( conv );
  else resetMetricsFor( conv );
}

async function patchWorkspace ( id: string, patch: Record<string, unknown> ): Promise<void>
{
  try
  {
    await api<WorkspaceDto>( 'PATCH', `/api/workspaces/${ id }`, patch );
  } catch ( e )
  {
    state.error = errorText( e );
  }
}

/** POSTs a new session in a workspace and selects it. */
async function createConversation ( workspaceId: string ): Promise<Conversation | undefined>
{
  const ws = findWorkspace( workspaceId );
  if ( !ws ) return undefined;
  try
  {
    const s = await api<SessionDto>( 'POST', `/api/workspaces/${ workspaceId }/sessions`, {} );
    const conv = upsertConversation( ws, s );
    applySessionDetails( conv, s );
    if ( !conv.loaded )
    {
      conv.messages = [];
      conv.loaded = true;
    }
    state.activeWorkspaceId = ws.id;
    state.activeConversationId = conv.id;
    resetMetricsFor( conv );
    if ( !ws.expanded )
    {
      ws.expanded = true;
      void patchWorkspace( ws.id, { expanded: true } );
    }
    return conv;
  } catch ( e )
  {
    state.error = errorText( e );
    return undefined;
  }
}

async function resync (): Promise<void>
{
  await loadWorkspaces();
  void refreshModels();
  const conv = activeConv();
  if ( conv?.loaded )
  {
    conv.loaded = false;
    await loadConversation( conv );
  }
}

// Relative "updated" labels age while the app stays open.
if ( typeof window !== 'undefined' )
{
  window.setInterval( () =>
  {
    for ( const ws of state.workspaces )
    {
      for ( const c of ws.conversations ) c.updatedAt = relativeTime( c.updatedAtIso );
    }
  }, 60_000 );
}

// --- live events --------------------------------------------------------------

function removeConversation ( id: string ): void
{
  for ( const ws of state.workspaces )
  {
    const index = ws.conversations.findIndex( ( c ) => c.id === id );
    if ( index !== -1 ) ws.conversations.splice( index, 1 );
  }
  if ( state.activeConversationId === id ) void ensureSelection();
}

function conversationForEvent ( event: SofiaEvent, fallbackId?: string ): Conversation | undefined
{
  return findConversation( event.sessionId ?? fallbackId );
}

function handleEvent ( event: SofiaEvent ): void
{
  if ( event.type === 'message.created' || event.type === 'message.updated' || event.type === 'message.deleted' )
  {
    const sid = event.sessionId ?? ( event.data as { sessionId?: string; } | null )?.sessionId;
    const deferred = sid ? deferredEvents.get( sid ) : undefined;
    if ( deferred )
    {
      deferred.push( event );
      return;
    }
  }
  switch ( event.type )
  {
    case 'workspace.updated': {
      upsertWorkspace( { ...( event.data as WorkspaceDto ), conversations: undefined } );
      break;
    }
    case 'workspace.deleted': {
      const { id } = event.data as { id: string; };
      const index = state.workspaces.findIndex( ( w ) => w.id === id );
      if ( index === -1 ) break;
      state.workspaces.splice( index, 1 );
      if ( state.activeWorkspaceId === id || !activeConv() )
      {
        if ( state.workspaces.length ) void ensureSelection();
        else void loadWorkspaces();
      }
      break;
    }
    case 'session.created':
    case 'session.updated': {
      const s = event.data as SessionDto;
      const ws = findWorkspace( s?.workspaceId ?? event.workspaceId ?? '' );
      if ( !ws )
      {
        scheduleRefresh();
        break;
      }
      const known = findConversation( s.id );
      const conv = known ?? upsertConversation( ws, s );
      applySessionDetails( conv, s );
      if ( known && event.type === 'session.updated' && conv.id === state.activeConversationId )
      {
        if ( s.permissionMode ) state.permissionMode = s.permissionMode;
        if ( s.reasoningMode ) state.reasoningMode = s.reasoningMode;
      }
      break;
    }
    case 'session.deleted': {
      const { id } = ( event.data ?? {} ) as { id?: string; };
      removeConversation( id ?? event.sessionId ?? '' );
      break;
    }
    case 'session.status': {
      const conv = conversationForEvent( event );
      const data = event.data as { status: SessionStatus; error?: string; };
      if ( conv && data?.status ) applyStatus( conv, data.status );
      break;
    }
    case 'session.metrics': {
      const conv = conversationForEvent( event );
      const data = event.data as MetricsDto;
      if ( !conv || !data ) break;
      conv.usage = data.usage;
      conv.context = data.context;
      if ( conv.id === state.activeConversationId ) applyMetrics( data );
      break;
    }
    case 'message.created':
    case 'message.updated': {
      const dto = event.data as MessageDto;
      const conv = conversationForEvent( event, dto?.sessionId );
      if ( !conv?.loaded || !dto ) break;
      const mapped = mapMessage( dto );
      const existing = conv.messages.find( ( m ) => m.id === dto.id );
      // A tool call that just finished may have changed files: let the workspace panel know.
      const before = new Map( ( existing?.toolCalls ?? [] ).map( ( tc ) => [ tc.id, tc.status ] ) );
      if ( ( mapped.toolCalls ?? [] ).some( ( tc ) => tc.status !== 'running' && before.get( tc.id ) !== tc.status ) )
      {
        toolActivity.tick += 1;
        toolActivity.workspaceId = conv.workspaceId;
      }
      if ( existing ) replaceInto( existing, mapped );
      else conv.messages.push( mapped );
      break;
    }
    case 'message.delta': {
      const data = event.data as { messageId: string; field: 'text' | 'reasoning'; delta: string; };
      if ( !data?.messageId || typeof data.delta !== 'string' ) break;
      const candidates = event.sessionId
        ? [ conversationForEvent( event ) ]
        : state.workspaces.flatMap( ( w ) => w.conversations );
      for ( const conv of candidates )
      {
        if ( !conv?.loaded ) continue;
        // Read through the reactive array so the append re-renders the card.
        const message = conv.messages.find( ( m ) => m.id === data.messageId );
        if ( !message ) continue;
        if ( data.field === 'reasoning' ) message.reasoning = ( message.reasoning ?? '' ) + data.delta;
        else message.text = ( message.text ?? '' ) + data.delta;
        break;
      }
      break;
    }
    case 'message.deleted': {
      const { ids } = ( event.data ?? {} ) as { ids?: string[]; };
      if ( !ids?.length ) break;
      const drop = new Set( ids );
      const targets = event.sessionId
        ? [ conversationForEvent( event ) ]
        : state.workspaces.flatMap( ( w ) => w.conversations );
      for ( const conv of targets )
      {
        if ( !conv?.loaded ) continue;
        if ( conv.messages.some( ( m ) => drop.has( m.id ) ) ) conv.messages = conv.messages.filter( ( m ) => !drop.has( m.id ) );
      }
      break;
    }
    case 'models.updated':
      void refreshModels();
      break;
    case 'resync':
      void resync();
      break;
    default:
      break;
  }
}

onServerEvent( handleEvent );

// ---------------------------------------------------------------------------

export function useHarness ()
{
  const activeWorkspace = computed(
    () => state.workspaces.find( ( w ) => w.id === state.activeWorkspaceId ) ?? state.workspaces[ 0 ],
  );

  const activeConversation = computed( () =>
  {
    const ws = activeWorkspace.value;
    return ws?.conversations.find( ( c ) => c.id === state.activeConversationId ) ?? ( ws ? firstVisible( ws ) : undefined );
  } );

  const messages = computed<Message[]>( () => activeConversation.value?.messages ?? [] );

  /** The active session's model (falls back to the workspace default). */
  const activeModelId = computed( {
    get: () => modelOf( activeConversation.value ) || activeWorkspace.value?.modelId || defaultModelId(),
    set: ( value: string ) =>
    {
      void patchActiveSession( { modelId: value } );
    },
  } );

  const activeModel = computed<ModelOption>( () =>
  {
    const id = activeModelId.value;
    return (
      models.value.find( ( m ) => m.id === id ) ?? {
        id,
        providerId: id.split( '/' )[ 0 ] ?? '',
        modelId: id.split( '/' ).slice( 1 ).join( '/' ),
        label: id || 'No model',
        provider: '',
        local: false,
        contextLength: 0,
        maxOutput: 0,
        context: '',
        connected: false,
        reasoning: false,
        vision: false,
        audio: false,
        video: false,
        enabled: true,
      }
    );
  } );

  /** PATCHes the active session (optimistic); returns an error or null. */
  async function patchActiveSession ( patch: {
    title?: string;
    modelId?: string;
    agent?: string;
    permissionMode?: PermissionModeId;
    reasoningMode?: ReasoningModeId;
  } ): Promise<string | null>
  {
    const conv = activeConversation.value;
    if ( !conv ) return 'No active conversation.';
    const previous = {
      title: conv.title,
      modelId: conv.modelId,
      agent: conv.agent,
      permissionMode: conv.permissionMode,
      reasoningMode: conv.reasoningMode,
    };
    Object.assign( conv, patch );
    try
    {
      const s = await api<SessionDto>( 'PATCH', `/api/sessions/${ conv.id }`, patch );
      if ( s ) applySessionDetails( conv, s );
      return null;
    } catch ( e )
    {
      Object.assign( conv, previous );
      state.error = errorText( e );
      return state.error;
    }
  }

  async function selectConversation ( workspaceId: string, conversationId: string ): Promise<void>
  {
    const conv = findConversation( conversationId );
    state.activeWorkspaceId = conv?.workspaceId || workspaceId;
    state.activeConversationId = conversationId;
    const ws = findWorkspace( state.activeWorkspaceId );
    if ( ws && !ws.expanded )
    {
      ws.expanded = true;
      void patchWorkspace( ws.id, { expanded: true } );
    }
    playSound( 'select' );
    if ( !conv ) return;
    if ( conv.status === 'done' ) setStatusLocal( conv, 'idle' );
    resetMetricsFor( conv );
    if ( !conv.loaded ) await loadConversation( conv );
  }

  /** Opens any session by id (e.g. a subagent child linked from a tool call). */
  async function openConversation ( sessionId: string ): Promise<void>
  {
    let conv = findConversation( sessionId );
    if ( !conv )
    {
      try
      {
        const s = await api<SessionDto>( 'GET', `/api/sessions/${ sessionId }` );
        const ws = findWorkspace( s.workspaceId );
        if ( !ws ) return;
        conv = upsertConversation( ws, s );
        applySessionDetails( conv, s );
        conv.messages = ( s.messages ?? [] ).map( mapMessage );
        conv.loaded = true;
      } catch ( e )
      {
        state.error = errorText( e );
        return;
      }
    }
    await selectConversation( conv.workspaceId, conv.id );
  }

  function toggleWorkspace ( workspaceId: string ): void
  {
    const ws = findWorkspace( workspaceId );
    if ( !ws ) return;
    ws.expanded = !ws.expanded;
    void patchWorkspace( ws.id, { expanded: ws.expanded } );
  }

  function setWorkspaceIcon ( workspaceId: string, icon: string ): void
  {
    const ws = findWorkspace( workspaceId );
    if ( !ws ) return;
    ws.icon = icon;
    void patchWorkspace( ws.id, { icon } );
  }

  function setWorkspaceModel ( workspaceId: string, modelId: string ): void
  {
    const ws = findWorkspace( workspaceId );
    if ( !ws ) return;
    ws.modelId = modelId;
    void patchWorkspace( ws.id, { modelId } );
  }

  /** Live rename; the PATCH is debounced while the user types. */
  function setWorkspaceName ( workspaceId: string, name: string ): void
  {
    const ws = findWorkspace( workspaceId );
    if ( !ws ) return;
    ws.name = name;
    const pending = renameTimers.get( workspaceId );
    if ( pending !== undefined ) window.clearTimeout( pending );
    renameTimers.set(
      workspaceId,
      window.setTimeout( () =>
      {
        renameTimers.delete( workspaceId );
        if ( name.trim() ) void patchWorkspace( workspaceId, { name: name.trim() } );
      }, 400 ),
    );
  }

  async function removeWorkspace ( workspaceId: string ): Promise<void>
  {
    try
    {
      await api( 'DELETE', `/api/workspaces/${ workspaceId }` );
    } catch ( e )
    {
      state.error = errorText( e );
      return;
    }
    const index = state.workspaces.findIndex( ( w ) => w.id === workspaceId );
    if ( index !== -1 ) state.workspaces.splice( index, 1 );
    if ( !state.workspaces.length ) await loadWorkspaces();
    else if ( state.activeWorkspaceId === workspaceId ) await ensureSelection();
  }

  /**
   * Parks a conversation in its workspace's "Archived" folder (nothing is
   * written to disk). Returns an error message or null.
   */
  async function archiveConversation ( conversationId: string ): Promise<string | null>
  {
    try
    {
      await api<SessionDto>( 'POST', `/api/sessions/${ conversationId }/archive` );
    } catch ( e )
    {
      state.error = errorText( e );
      return state.error;
    }
    const conv = findConversation( conversationId );
    if ( conv ) conv.archived = true;
    // The open conversation cannot stay in the archive: move to the next one.
    if ( conversationId === state.activeConversationId ) await ensureSelection();
    return null;
  }

  /** Renames any conversation (optimistic); returns an error or null. */
  async function renameConversation ( conversationId: string, title: string ): Promise<string | null>
  {
    const conv = findConversation( conversationId );
    const next = title.trim();
    if ( !conv || !next || next === conv.title ) return null;
    const previous = conv.title;
    conv.title = next;
    try
    {
      await api<SessionDto>( 'PATCH', `/api/sessions/${ conversationId }`, { title: next } );
      return null;
    } catch ( e )
    {
      conv.title = previous;
      state.error = errorText( e );
      return state.error;
    }
  }

  /** Pins a conversation to the top of its list, or releases it. */
  async function pinConversation ( conversationId: string, pinned: boolean ): Promise<string | null>
  {
    try
    {
      await api<SessionDto>( 'POST', `/api/sessions/${ conversationId }/${ pinned ? 'pin' : 'unpin' }` );
    } catch ( e )
    {
      state.error = errorText( e );
      return state.error;
    }
    const conv = findConversation( conversationId );
    if ( conv ) setPinnedLocal( conv, pinned );
    return null;
  }

  /** Stores a manual order for a workspace's visible conversations (`orderedIds`: first = top). */
  async function reorderConversations ( workspaceId: string, orderedIds: string[] ): Promise<string | null>
  {
    const ws = findWorkspace( workspaceId );
    if ( !ws ) return null;
    const byId = new Map( ws.conversations.map( ( c ) => [ c.id, c ] ) );
    const ordered = orderedIds.map( ( id ) => byId.get( id ) ).filter( ( c ): c is Conversation => !!c );
    const moved = new Set( ordered.map( ( c ) => c.id ) );
    let next = 0;
    ws.conversations = ws.conversations.map( ( c ) => ( moved.has( c.id ) ? ordered[ next++ ] : c ) );
    try
    {
      await api( 'PUT', `/api/workspaces/${ workspaceId }/sessions/order`, { ids: orderedIds } );
    } catch ( e )
    {
      state.error = errorText( e );
      return state.error;
    }
    return null;
  }

  /**
   * Stores the manual order of the sidebar tree (`orderedIds`: the workspace
   * folder section first, then the chat groups — first id = top of its section).
   */
  async function reorderWorkspaces ( orderedIds: string[] ): Promise<string | null>
  {
    const byId = new Map( state.workspaces.map( ( w ) => [ w.id, w ] ) );
    orderedIds.forEach( ( id, index ) =>
    {
      const ws = byId.get( id );
      if ( ws ) ws.order = index + 1;
    } );
    sortWorkspaces();
    try
    {
      await api( 'PUT', '/api/workspaces/order', { ids: orderedIds } );
    } catch ( e )
    {
      state.error = errorText( e );
      return state.error;
    }
    return null;
  }

  /** Brings an archived conversation back into its workspace's list. */
  async function restoreConversation ( conversationId: string ): Promise<string | null>
  {
    try
    {
      await api<SessionDto>( 'POST', `/api/sessions/${ conversationId }/unarchive` );
    } catch ( e )
    {
      state.error = errorText( e );
      return state.error;
    }
    const conv = findConversation( conversationId );
    if ( conv ) conv.archived = false;
    return null;
  }

  /** Removes a conversation for good (messages, snapshots and children too). */
  async function deleteConversation ( conversationId: string ): Promise<string | null>
  {
    try
    {
      await api( 'DELETE', `/api/sessions/${ conversationId }` );
    } catch ( e )
    {
      state.error = errorText( e );
      return state.error;
    }
    removeConversation( conversationId );
    return null;
  }

  /** Selects the first conversation of a workspace, creating one if needed. */
  async function focusWorkspace ( ws: Workspace ): Promise<void>
  {
    const conv = firstVisible( ws );
    if ( conv ) await selectConversation( ws.id, conv.id );
    else await createConversation( ws.id );
  }

  /** Adds a folder workspace; returns an error message or null. */
  async function addWorkspace ( path: string ): Promise<string | null>
  {
    let trimmed = path.trim().replace( /[\\/]+$/, '' );
    if ( /^[A-Za-z]:$/.test( trimmed ) ) trimmed += '\\';
    if ( !trimmed ) return 'Enter a folder path.';
    try
    {
      const dto = await api<WorkspaceDto>( 'POST', '/api/workspaces', {
        kind: 'folder',
        path: trimmed,
        modelId: defaultModelId() || undefined,
      } );
      await focusWorkspace( upsertWorkspace( dto ) );
      return null;
    } catch ( e )
    {
      return errorText( e );
    }
  }

  /** Focuses the workspace for `path`, adding it when it is not in the tree. */
  async function openWorkspaceAt ( path: string ): Promise<void>
  {
    const existing = state.workspaces.find( ( w ) => w.kind !== 'chat' && samePath( w.path, path ) );
    if ( existing )
    {
      await focusWorkspace( existing );
      return;
    }
    const err = await addWorkspace( path );
    if ( err ) state.error = err;
  }

  /** Adds a chat group — conversations not tied to a folder on disk. */
  async function addChatGroup ( name?: string ): Promise<void>
  {
    try
    {
      const dto = await api<WorkspaceDto>( 'POST', '/api/workspaces', {
        kind: 'chat',
        name: name?.trim() || 'New group',
        icon: 'chat',
        modelId: defaultModelId() || undefined,
      } );
      await focusWorkspace( upsertWorkspace( dto ) );
    } catch ( e )
    {
      state.error = errorText( e );
    }
  }

  async function newConversationIn ( workspaceId: string ): Promise<void>
  {
    await createConversation( workspaceId );
  }

  async function newConversation (): Promise<void>
  {
    const id = activeWorkspace.value?.id;
    if ( id ) await createConversation( id );
  }

  /** Queues picked files for the next prompt, with object URLs for previews. */
  function addFiles ( files: File[] ): void
  {
    for ( const file of files )
    {
      const kind = kindFromMime( file.type );
      state.attachments.push( {
        id: uid( 'a' ),
        name: file.name,
        kind,
        size: formatBytes( file.size ),
        mime: file.type,
        previewUrl: kind === 'image' || kind === 'video' ? URL.createObjectURL( file ) : undefined,
        file,
      } );
    }
  }

  function removeAttachment ( id: string ): void
  {
    const index = state.attachments.findIndex( ( a ) => a.id === id );
    if ( index === -1 ) return;
    const [ removed ] = state.attachments.splice( index, 1 );
    if ( removed?.previewUrl ) URL.revokeObjectURL( removed.previewUrl );
  }

  function clearAttachments (): void
  {
    for ( const a of state.attachments )
    {
      if ( a.previewUrl ) URL.revokeObjectURL( a.previewUrl );
    }
    state.attachments = [];
  }

  /**
   * Uploads pending attachments and prompts the active session. Messages are
   * not added locally: the server emits `message.created` for the user turn
   * and the replies.
   */
  async function sendMessage (): Promise<void>
  {
    const text = state.composerText.trim();
    const pending = [ ...state.attachments ];
    if ( ( !text && !pending.length ) || state.isReplying || sendingPrompt ) return;
    const conv = activeConversation.value;
    if ( !conv ) return;

    state.composerText = '';
    state.attachments = [];
    await submitPrompt( conv, text, pending, true );
  }

  /**
   * Uploads `pending` and prompts `conv`. On failure the prompt is handed back
   * to its chat's composer (`restore`), and an error card is shown.
   */
  async function submitPrompt ( conv: Conversation, text: string, pending: PendingAttachment[], restore: boolean ): Promise<void>
  {
    if ( sendingPrompt ) return;
    sendingPrompt = true;
    const previous = conv.status;
    setStatusLocal( conv, 'working' );
    playSound( 'send' );
    // Prompting an archived conversation brings it back into the list.
    if ( conv.archived ) void restoreConversation( conv.id );
    try
    {
      const ids: string[] = [];
      for ( const a of pending ) ids.push( a.uploadedId ?? ( await uploadAttachment( a.file! ) ).id );
      await api( 'POST', `/api/sessions/${ conv.id }/prompt`, {
        text,
        attachments: ids.length ? ids : undefined,
        delivery: 'prompt',
        modelId: modelOf( conv ) || undefined,
        permissionMode: conv.permissionMode ?? state.permissionMode,
        reasoningMode: conv.reasoningMode ?? state.reasoningMode,
        language: settings.language,
      } );
      for ( const a of pending ) if ( a.previewUrl ) URL.revokeObjectURL( a.previewUrl );
    } catch ( e )
    {
      if ( conv.status === 'working' ) setStatusLocal( conv, previous === 'done' ? 'idle' : previous );
      if ( restore ) restoreDraft( conv, text, pending );
      pushLocalError( conv, errorText( e ) );
    } finally
    {
      sendingPrompt = false;
    }
  }

  /** Follow-up while a run is going: steer (mid-run) or queue (afterwards). */
  async function sendSteered ( text: string, delivery: 'steer' | 'queue' ): Promise<string | null>
  {
    const conv = activeConversation.value;
    if ( !conv ) return 'No active conversation.';
    playSound( 'send' );
    try
    {
      const pending = [ ...state.attachments ];
      const ids: string[] = [];
      for ( const a of pending ) ids.push( a.uploadedId ?? ( await uploadAttachment( a.file! ) ).id );
      await api( 'POST', `/api/sessions/${ conv.id }/prompt`, {
        text,
        attachments: ids.length ? ids : undefined,
        delivery,
        modelId: modelOf( conv ) || undefined,
        permissionMode: conv.permissionMode ?? state.permissionMode,
        reasoningMode: conv.reasoningMode ?? state.reasoningMode,
        language: settings.language,
      } );
      for ( const a of pending ) if ( a.previewUrl ) URL.revokeObjectURL( a.previewUrl );
      state.attachments = state.attachments.filter( ( a ) => !pending.includes( a ) );
      return null;
    } catch ( e )
    {
      return errorText( e );
    }
  }

  /** Runs a prompt-template command in the active session. */
  async function runCommand ( name: string, argText: string ): Promise<string | null>
  {
    const conv = activeConversation.value;
    if ( !conv ) return 'No active conversation.';
    if ( state.isReplying ) return 'Wait for the current reply to finish.';
    const previous = conv.status;
    const pending = [ ...state.attachments ];
    state.composerText = '';
    state.attachments = [];
    setStatusLocal( conv, 'working' );
    playSound( 'send' );
    try
    {
      const ids: string[] = [];
      for ( const a of pending ) ids.push( a.uploadedId ?? ( await uploadAttachment( a.file! ) ).id );
      await api( 'POST', `/api/sessions/${ conv.id }/command`, {
        name,
        arguments: argText || undefined,
        attachments: ids.length ? ids : undefined,
      } );
      for ( const a of pending ) if ( a.previewUrl ) URL.revokeObjectURL( a.previewUrl );
      return null;
    } catch ( e )
    {
      restoreDraft( conv, `/${ name }${ argText ? ' ' + argText : '' }`, pending );
      if ( conv.status === 'working' ) setStatusLocal( conv, previous === 'done' ? 'idle' : previous );
      const message = errorText( e );
      pushLocalError( conv, message );
      return message;
    }
  }

  async function cancelReply (): Promise<void>
  {
    const conv = findConversation( state.replyingConversationId ?? undefined ) ?? activeConversation.value;
    if ( !conv ) return;
    try
    {
      await api( 'POST', `/api/sessions/${ conv.id }/abort` );
    } catch ( e )
    {
      pushLocalError( conv, errorText( e ) );
    }
  }

  function restoreToComposer ( message: Message ): void
  {
    clearAttachments();
    state.composerText = message.text;
    state.attachments = ( message.attachments ?? [] ).map( ( a ) => ( {
      ...a,
      uploadedId: a.id,
      previewUrl: a.kind === 'image' || a.kind === 'video' ? a.previewUrl ?? ( a.url ? serverUrl( a.url ) : undefined ) : undefined,
    } ) );
  }

  /**
   * Reverts the session to before `messageId` (the server restores files and
   * emits `message.deleted`). A reverted user message goes back to the
   * composer, attachments included, so it can be edited and resubmitted.
   * Returns an error message or null.
   */
  async function revertMessage ( messageId: string ): Promise<string | null>
  {
    const conv = activeConversation.value;
    if ( !conv ) return 'No active conversation.';
    if ( state.isReplying ) return 'Stop the running reply before reverting.';
    const target = conv.messages.find( ( m ) => m.id === messageId );
    const error = await revertTo( conv, messageId );
    if ( !error && target?.role === 'user' ) restoreToComposer( target );
    return error;
  }

  /**
   * Forks the conversation at `messageId` into a new one (history up to and
   * including that message) and opens it. Returns an error message or null.
   */
  async function branchMessage ( messageId: string ): Promise<string | null>
  {
    const conv = activeConversation.value;
    if ( !conv ) return 'No active conversation.';
    if ( state.isReplying ) return 'Stop the running reply before branching.';
    try
    {
      const s = await api<SessionDto>( 'POST', `/api/sessions/${ conv.id }/branch`, { messageId } );
      await openConversation( s.id );
      return null;
    } catch ( e )
    {
      return errorText( e );
    }
  }

  /** Server-side revert to before `messageId`; drops the removed messages locally. */
  async function revertTo ( conv: Conversation, messageId: string ): Promise<string | null>
  {
    try
    {
      const res = await api<{ removed: string[]; restoredFiles: string[]; }>(
        'POST',
        `/api/sessions/${ conv.id }/revert`,
        { messageId },
      );
      const drop = new Set( res?.removed ?? [] );
      if ( drop.size ) conv.messages = conv.messages.filter( ( m ) => !drop.has( m.id ) );
      return null;
    } catch ( e )
    {
      return errorText( e );
    }
  }

  /**
   * Retries a failed reply: rewinds the session to the user message that
   * triggered it (files restored) and sends that prompt again. A local error
   * card (the prompt never reached the server) is just dismissed and the
   * prompt still waiting in the composer is resubmitted.
   */
  async function retryMessage ( messageId: string ): Promise<string | null>
  {
    const conv = activeConversation.value;
    if ( !conv ) return 'No active conversation.';
    if ( state.isReplying || sendingPrompt ) return 'Wait for the current reply to finish.';
    const index = conv.messages.findIndex( ( m ) => m.id === messageId );
    if ( index === -1 ) return 'Message not found.';

    if ( conv.messages[ index ].local )
    {
      conv.messages = conv.messages.filter( ( m ) => m.id !== messageId );
      await sendMessage();
      return null;
    }

    const trigger = conv.messages.slice( 0, index ).reverse().find( ( m ) => m.role === 'user' );
    if ( !trigger ) return 'There is no prompt to retry.';
    const text = trigger.text;
    const attachments: PendingAttachment[] = ( trigger.attachments ?? [] ).map( ( a ) => ( { ...a, uploadedId: a.id } ) );
    const error = await revertTo( conv, trigger.id );
    if ( error ) return error;
    await submitPrompt( conv, text, attachments, false );
    return null;
  }

  /** Resolves a permission card (optimistic; reverted when the reply fails). */
  async function resolvePermissionRequest ( message: Message, choice: string ): Promise<void>
  {
    const p = message.permission;
    if ( !p || p.resolved ) return;
    p.resolved = choice;
    try
    {
      await api( 'POST', `/api/sessions/${ message.sessionId }/permissions/${ p.requestId }`, {
        decision: permissionDecision( choice ),
      } );
    } catch ( e )
    {
      p.resolved = undefined;
      state.error = errorText( e );
    }
  }

  async function answerFormQuestion ( message: Message, option: string ): Promise<void>
  {
    const q = message.question;
    if ( !q || q.resolved ) return;
    q.resolved = option;
    try
    {
      await api( 'POST', `/api/sessions/${ message.sessionId }/questions/${ q.formId }`, {
        answers: [ q.multi ? [ option ] : option ],
      } );
    } catch ( e )
    {
      // The run that asked is gone (stopped or restarted): the card can never be answered.
      if ( isStalePrompt( e ) )
      {
        q.resolved = 'Expired';
        return;
      }
      q.resolved = undefined;
      state.error = errorText( e );
    }
  }

  function isStalePrompt ( e: unknown ): boolean
  {
    return e instanceof Error && /no longer pending/i.test( e.message );
  }

  async function confirmFormQuestions ( message: Message, picks: string[][] ): Promise<void>
  {
    const qs = message.questionSet;
    if ( !qs || qs.confirmed ) return;
    const previousAnswers = qs.answers;
    const answers = qs.questions.map( ( q, i ) =>
    {
      const chosen = picks[ i ] ?? [];
      if ( chosen.length === 0 ) return null;
      return q.multi ? chosen : chosen[ 0 ];
    } );
    qs.answers = answers.map( ( a ) => ( Array.isArray( a ) ? a.join( ', ' ) : a ) );
    qs.confirmed = true;
    try
    {
      await api( 'POST', `/api/sessions/${ message.sessionId }/questions/${ qs.formId }`, { answers } );
    } catch ( e )
    {
      if ( isStalePrompt( e ) )
      {
        qs.answers = [];
        return;
      }
      qs.confirmed = false;
      qs.answers = previousAnswers;
      state.error = errorText( e );
    }
  }

  return {
    state,
    metrics,
    toolActivity,
    models,
    refreshLiveSessions: loadWorkspaces,
    refreshModels,
    defaultModelId,
    activeWorkspace,
    activeConversation,
    messages,
    activeModel,
    activeModelId,
    patchActiveSession,
    selectConversation,
    openConversation,
    archiveConversation,
    pinConversation,
    renameConversation,
    reorderConversations,
    reorderWorkspaces,
    restoreConversation,
    deleteConversation,
    toggleWorkspace,
    setWorkspaceIcon,
    setWorkspaceModel,
    setWorkspaceName,
    removeWorkspace,
    addWorkspace,
    openWorkspaceAt,
    addChatGroup,
    newConversation,
    newConversationIn,
    addFiles,
    removeAttachment,
    clearAttachments,
    sendMessage,
    sendSteered,
    runCommand,
    cancelReply,
    revertMessage,
    branchMessage,
    retryMessage,
    resolvePermissionRequest,
    answerFormQuestion,
    confirmFormQuestions,
  };
}
