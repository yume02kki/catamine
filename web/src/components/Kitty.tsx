// The KittyParty cat: dotted ASCII, glowing. Its eyes follow the state of things.
export type Mood = "idle" | "happy" | "alert" | "sleepy";

const EYES: Record<Mood, string> = {
  idle: "  :  .   . :",
  happy: "  :  ^   ^ :",
  alert: "  :  o   o :",
  sleepy: "  :  -   - :",
};

const BODY = (eyes: string) => [
  "  .       .",
  "  :'.   .':",
  "  :  '''  :",
  eyes,
  "  '.  ..  .'",
  "   .'    '.",
  "  :        :    .",
  "  :  ..    :   .'",
  "  :  ::    :  .'",
  "  '..''...''''",
].join("\n");

const FACE = (mood: Mood) => {
  const e = mood === "happy" ? "^ ^" : mood === "alert" ? "o o" : mood === "sleepy" ? "- -" : ". .";
  return [":'. .':", `: ${e} :`, "'. ^ .'"].join("\n");
};

export function Kitty({ mood = "idle", size = "lg", label }: { mood?: Mood; size?: "lg" | "sm" | "face"; label?: string }) {
  return (
    <pre className={`kitty kitty-${size} kitty-${mood}`} role="img" aria-label={label ?? `KittyParty cat (${mood})`}>
      {size === "face" ? FACE(mood) : size === "sm" ? BODY(EYES[mood]).split("\n").slice(0, 6).join("\n") : BODY(EYES[mood])}
    </pre>
  );
}
