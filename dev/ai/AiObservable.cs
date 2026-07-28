using Godot;

// DEV-ONLY visual-test harness (res://dev/ai). Inert in normal play — only the `-- --scenario <name>` launch path (see
// AiTestRunner.TryBoot, called from Game._Ready) activates any of it. Keep production systems decoupled: a node opts IN to
// observation by joining the "ai_observable" group AND implementing IAiObservable; the harness never reaches into internals.
namespace Grove.Dev.Ai
{
    // A node exposes a small, SEMANTIC debug snapshot (not a raw property dump) for the AI test runner to serialize.
    public interface IAiObservable
    {
        Godot.Collections.Dictionary GetAiDebugState();
    }

    public static class AiObservable
    {
        public const string Group = "ai_observable";

        // Collect { nodeName -> state } from every opted-in node. Adds node_path to each so captures are traceable.
        public static Godot.Collections.Dictionary CollectActors(SceneTree tree)
        {
            var actors = new Godot.Collections.Dictionary();
            if (tree == null) return actors;
            foreach (Node n in tree.GetNodesInGroup(Group))
            {
                if (n is not IAiObservable obs) continue;
                Godot.Collections.Dictionary st;
                try { st = obs.GetAiDebugState() ?? new Godot.Collections.Dictionary(); }
                catch (System.Exception e) { st = new Godot.Collections.Dictionary { { "error", e.Message } }; }
                st["node_path"] = n.GetPath().ToString();
                actors[n.Name] = st;
            }
            return actors;
        }
    }
}
