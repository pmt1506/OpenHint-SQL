using System;
using System.Collections.Generic;

namespace OpenHintSQL.Schema
{
    /// <summary>
    /// A generic trie (prefix tree) for O(k) prefix search, where k is the prefix length.
    /// All keys are stored in lowercase for case-insensitive matching.
    /// </summary>
    /// <typeparam name="T">The type of value stored at each leaf node.</typeparam>
    internal class TrieIndex<T>
    {
        private readonly TrieNode _root = new TrieNode();
        private int _count;

        /// <summary>
        /// Number of items stored in the trie.
        /// </summary>
        public int Count => _count;

        /// <summary>
        /// Inserts a key-value pair into the trie.
        /// The key is normalized to lowercase for case-insensitive matching.
        /// </summary>
        /// <param name="key">The search key (e.g. object name).</param>
        /// <param name="value">The value associated with this key.</param>
        public void Insert(string key, T value)
        {
            if (string.IsNullOrEmpty(key))
                return;

            var node = _root;
            var lowerKey = key.ToLowerInvariant();

            for (int i = 0; i < lowerKey.Length; i++)
            {
                var ch = lowerKey[i];
                if (!node.Children.TryGetValue(ch, out var child))
                {
                    child = new TrieNode();
                    node.Children[ch] = child;
                }
                node = child;
            }

            node.Values.Add(value);
            _count++;
        }

        /// <summary>
        /// Returns all values whose keys start with the given prefix.
        /// The prefix is normalized to lowercase for case-insensitive matching.
        /// </summary>
        /// <param name="prefix">The prefix to search for.</param>
        /// <returns>A list of all matching values. Empty list if no matches.</returns>
        public List<T> Search(string prefix)
        {
            var results = new List<T>();

            if (string.IsNullOrEmpty(prefix))
                return results;

            var node = _root;
            var lowerPrefix = prefix.ToLowerInvariant();

            // Navigate to the node representing the end of the prefix
            for (int i = 0; i < lowerPrefix.Length; i++)
            {
                if (!node.Children.TryGetValue(lowerPrefix[i], out node))
                    return results; // Prefix not found — no matches
            }

            // Collect all values in the subtree rooted at this node
            CollectValues(node, results);
            return results;
        }

        /// <summary>
        /// Removes all entries from the trie.
        /// </summary>
        public void Clear()
        {
            _root.Children.Clear();
            _root.Values.Clear();
            _count = 0;
        }

        /// <summary>
        /// Recursively collects all values from the given node and its descendants.
        /// </summary>
        private void CollectValues(TrieNode node, List<T> results)
        {
            if (node.Values.Count > 0)
            {
                results.AddRange(node.Values);
            }

            foreach (var child in node.Children.Values)
            {
                CollectValues(child, results);
            }
        }

        /// <summary>
        /// Internal trie node holding child branches and any values stored at this key.
        /// </summary>
        private class TrieNode
        {
            public Dictionary<char, TrieNode> Children { get; } = new Dictionary<char, TrieNode>();
            public List<T> Values { get; } = new List<T>();
        }
    }
}
