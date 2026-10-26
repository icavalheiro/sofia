<script setup lang="ts">
// Icon facade over Lucide. Components address icons by a short semantic key
// (also persisted for workspace icons), and this map resolves the key to the
// Lucide component, so the whole app can be re-skinned from one place.
import { computed, type Component } from 'vue'
import {
  Archive,
  ArchiveRestore,
  Bell,
  BookOpen,
  Bot,
  Box,
  Briefcase,
  Camera,
  Check,
  ChevronDown,
  ChevronLeft,
  ChevronRight,
  ChevronUp,
  CircleQuestionMark,
  CircleStop,
  Clock,
  Code,
  Copy,
  Cpu,
  Database,
  DollarSign,
  File,
  Files,
  FlaskConical,
  Folder,
  FolderOpen,
  GitBranch,
  Globe,
  Image,
  Info,
  Keyboard,
  Languages,
  Layers,
  Link,
  LoaderCircle,
  MessageSquare,
  Music,
  PanelRight,
  Palette,
  Paperclip,
  Pin,
  PinOff,
  Plug,
  Plus,
  RefreshCw,
  RotateCw,
  Search,
  Send,
  Server,
  Settings,
  Shield,
  SlidersVertical,
  Sparkles,
  Star,
  Terminal,
  Trash2,
  TriangleAlert,
  Undo2,
  Upload,
  User,
  Video,
  X,
  Zap,
} from 'lucide-vue-next'

const props = defineProps<{ name: string; size?: number | string; strokeWidth?: number; spin?: boolean }>()

const ICONS: Record<string, Component> = {
  folder: Folder,
  folderOpen: FolderOpen,
  files: Files,
  file: File,
  plus: Plus,
  chat: MessageSquare,
  send: Send,
  paperclip: Paperclip,
  image: Image,
  video: Video,
  sliders: SlidersVertical,
  chevronRight: ChevronRight,
  chevronLeft: ChevronLeft,
  chevronDown: ChevronDown,
  chevronUp: ChevronUp,
  sparkles: Sparkles,
  shield: Shield,
  zap: Zap,
  stop: CircleStop,
  trash: Trash2,
  archive: Archive,
  restore: ArchiveRestore,
  pin: Pin,
  unpin: PinOff,
  search: Search,
  x: X,
  user: User,
  gear: Settings,
  check: Check,
  code: Code,
  copy: Copy,
  book: BookOpen,
  palette: Palette,
  flask: FlaskConical,
  briefcase: Briefcase,
  globe: Globe,
  terminal: Terminal,
  database: Database,
  server: Server,
  star: Star,
  music: Music,
  camera: Camera,
  layers: Layers,
  cpu: Cpu,
  box: Box,
  bell: Bell,
  info: Info,
  refresh: RefreshCw,
  retry: RotateCw,
  undo: Undo2,
  link: Link,
  loader: LoaderCircle,
  plug: Plug,
  clock: Clock,
  dollar: DollarSign,
  panelRight: PanelRight,
  bot: Bot,
  alert: TriangleAlert,
  help: CircleQuestionMark,
  upload: Upload,
  languages: Languages,
  keyboard: Keyboard,
  branch: GitBranch,
}

const icon = computed(() => ICONS[props.name])
const boxSize = computed(() =>
  typeof props.size === 'number' ? `${props.size}px` : (props.size ?? '1.125rem'),
)
</script>

<template>
  <component
    :is="icon"
    v-if="icon"
    :class="{ 'sofia-icon--spin': spin }"
    :size="boxSize"
    :stroke-width="strokeWidth ?? 1.75"
    aria-hidden="true"
    focusable="false"
  />
</template>

<style scoped>
.sofia-icon--spin {
  animation: sofia-icon-spin 0.9s linear infinite;
}

@keyframes sofia-icon-spin {
  to {
    transform: rotate(360deg);
  }
}
</style>
