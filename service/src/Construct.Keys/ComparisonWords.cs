using System.Security.Cryptography;

namespace Construct.Keys;

/// <summary>
/// The words both screens show while pairing (8.3): six words, 48 bits of SHA-256 over the new
/// device's public key. The new device computes them from its own key, the approver from the key the
/// store hands it; if the store swapped the key, the words differ.
/// </summary>
public static class ComparisonWords
{
    public const int Count = 6;

    private static readonly string[] Words =
    [
        "acid", "acorn", "actor", "adult", "agent", "alarm", "album", "alien", "alley", "amber", "anchor", "angle", 
        "ankle", "apple", "apron", "arena", "armor", "arrow", "atlas", "attic", "autumn", "award", "bacon", "badge", 
        "bagel", "baker", "bamboo", "banana", "banjo", "barrel", "basket", "beach", "beacon", "beard", "beaver", "bench", 
        "berry", "bicycle", "bishop", "blade", "blanket", "blossom", "border", "bottle", "bowl", "brain", "branch", "bread", 
        "brick", "bridge", "broom", "bubble", "bucket", "buffalo", "bundle", "butter", "button", "cabin", "cable", "cactus", 
        "camel", "camera", "candle", "canoe", "canvas", "canyon", "carbon", "carpet", "carrot", "castle", "cattle", "cave", 
        "cellar", "cement", "chain", "chalk", "cherry", "chess", "chimney", "cider", "circle", "citrus", "clock", "cloud", 
        "clover", "coast", "cobra", "cocoa", "coffee", "comet", "copper", "coral", "cotton", "cougar", "cousin", "crane", 
        "crater", "crayon", "cricket", "crown", "crystal", "cup", "curtain", "cushion", "daisy", "dancer", "delta", "desert", 
        "diamond", "dinner", "dolphin", "donkey", "dragon", "drawer", "dream", "drum", "eagle", "earth", "echo", "eclipse", 
        "elbow", "ember", "engine", "falcon", "feather", "fence", "ferry", "fiber", "field", "finger", "flame", "flute", 
        "forest", "fossil", "fountain", "fox", "frost", "garden", "garlic", "giant", "ginger", "glacier", "globe", "glove", 
        "goat", "gravel", "guitar", "hammer", "harbor", "harvest", "hazel", "helmet", "hermit", "honey", "horizon", "hornet", 
        "island", "ivory", "jacket", "jaguar", "jelly", "jungle", "kettle", "kitten", "ladder", "lagoon", "lantern", "laser", 
        "lemon", "lentil", "letter", "lily", "lion", "lizard", "lobster", "magnet", "mango", "maple", "marble", "meadow", 
        "melon", "mirror", "monkey", "moon", "mosaic", "motor", "mountain", "muffin", "needle", "nest", "noodle", "oasis", 
        "ocean", "olive", "onion", "orange", "orbit", "orchid", "otter", "oyster", "paddle", "palace", "panda", "paper", 
        "parrot", "peach", "pebble", "pencil", "pepper", "piano", "pickle", "pigeon", "pillow", "pilot", "planet", "plum", 
        "pocket", "pony", "potato", "prism", "pumpkin", "puzzle", "quartz", "quill", "rabbit", "radar", "radio", "raven", 
        "ribbon", "river", "robot", "rocket", "saddle", "salmon", "sandal", "satin", "scarf", "shadow", "shell", "silver", 
        "sketch", "sled", "spider", "spoon", "squash", "statue", "stone", "storm", "sugar", "summit", "sunset", "swan", 
        "table", "tiger", "timber", "tomato",
    ];

    public static IReadOnlyList<string> For(ParticipantPublicKey key)
    {
        var hash = SHA256.HashData([.. "construct-words-v1\n"u8, .. key.Bytes]);
        return hash.Take(Count).Select(b => Words[b]).ToArray();
    }
}
