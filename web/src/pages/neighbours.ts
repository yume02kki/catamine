// Remembers the order of the traffic list the user came from, so the diff page can offer previous/next.
let state: { missionId: string; ids: string[]; search: string } = { missionId: "", ids: [], search: "" };

export function setNeighbours(missionId: string, ids: string[], search: string) {
  state = { missionId, ids, search };
}

export function neighbours(missionId: string, id: string): { prev: string | null; next: string | null; search: string } {
  if (state.missionId !== missionId) return { prev: null, next: null, search: "" };
  const i = state.ids.indexOf(id);
  if (i < 0) return { prev: null, next: null, search: state.search };
  return { prev: i > 0 ? state.ids[i - 1] : null, next: i < state.ids.length - 1 ? state.ids[i + 1] : null, search: state.search };
}
