using System;
using System.Security.Cryptography;
using System.Text;

namespace Mahjong.Domain.Cheat
{
    /// <summary>Stable character IDs used by local match rules and persisted state.</summary>
    public static class PlayerCharacterIds
    {
        public const string Player = "player";
        public const string Octopus = "octopus";
        public const int CurrentTraitsVersion = 1;

        public static bool IsSupported(string id) => id == Player || id == Octopus;
    }
    /// <summary>Deterministic, request-bound Octopus trait adjudication.</summary>
    public static class PlayerCharacterRules
    {
        public const int SleeveBloomThirdCandidatePercent = 10;

        public static bool GrantsThirdSleeveCandidate(
            string characterId,
            int traitsVersion,
            string roundId,
            int seat,
            int turnNumber,
            string requestId)
        {
            if (characterId != PlayerCharacterIds.Octopus || traitsVersion != PlayerCharacterIds.CurrentTraitsVersion)
                return false;
            if (string.IsNullOrWhiteSpace(roundId) || seat < 0 || seat > 3 || turnNumber < 0 || string.IsNullOrWhiteSpace(requestId))
                throw new ArgumentException("A character trait adjudication needs stable round, seat, turn and request identity.");

            byte[] input = Encoding.UTF8.GetBytes(roundId + "\n" + seat + "\n" + turnNumber + "\n" + requestId);
            byte[] digest;
            using (SHA256 sha = SHA256.Create()) digest = sha.ComputeHash(input);
            uint bucket = ((uint)digest[0] << 24) | ((uint)digest[1] << 16) | ((uint)digest[2] << 8) | digest[3];
            return IsSleeveBloomThirdCandidateHit((int)(bucket % 100U));
        }

        public static bool IsSleeveBloomThirdCandidateHit(int bucket) =>
            bucket >= 0 && bucket < 100 && bucket < SleeveBloomThirdCandidatePercent;
    }
}
