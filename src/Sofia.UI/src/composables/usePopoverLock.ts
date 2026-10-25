import { ref } from 'vue'

// One dropdown is open at a time across the composer chips (model, perms,
// reasoning) and the agent picker: opening one closes the previous owner, even
// when it is opened through a keyboard shortcut instead of a click.
const owner = ref<symbol | null>(null)

/** Binds a component to the single-open lock: read `isOpen`, call `claim()`/`release()`. */
export function usePopoverLock() {
  const id = Symbol('popover')

  const isOpen = () => owner.value === id

  /** Takes the lock from whoever holds it. Returns false when already ours. */
  function claim(): boolean {
    if (owner.value === id) return false
    owner.value = id
    return true
  }

  /** Drops the lock only when this component owns it. */
  function release(): void {
    if (owner.value === id) owner.value = null
  }

  return { isOpen, claim, release }
}
