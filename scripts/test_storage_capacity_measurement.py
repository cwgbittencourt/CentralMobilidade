import gzip
import math
import unittest
from storage_capacity_measurement import corpus, project


class StorageProjectionTests(unittest.TestCase):
    def test_exact_event_counts_and_distinct_retention_windows(self):
        self.assertEqual(project(1, 5000, 60), 25920000000)
        self.assertEqual(project(1, 5000, 365 * 5), 788400000000)
        self.assertEqual(project(1, 5000, 30), 12960000000)
        self.assertEqual(project(1, 5000, 7), 3024000000)

    def test_missing_or_invalid_measurement_is_not_zero_capacity(self):
        for value in (None, 0, -1, math.nan, math.inf):
            with self.subTest(value=value), self.assertRaises(ValueError):
                project(value, 5000, 60)

    def test_rate_and_retention_must_be_positive(self):
        for rate, days in ((0, 60), (5000, 0), (-1, 30), (math.inf, 60)):
            with self.subTest(rate=rate, days=days), self.assertRaises(ValueError):
                project(12, rate, days)

    def test_real_gzip_preserves_both_bounded_corpora(self):
        batches = list(corpus("synthetic"))
        self.assertEqual(len(batches), 40)
        self.assertEqual(sum(len(raw.splitlines()) for raw, _, _ in batches), 20000)
        self.assertEqual({group for _, _, group in batches}, {"regular", "varied"})
        for raw, payload, _ in batches:
            self.assertEqual(gzip.decompress(payload), raw)
            self.assertLess(len(payload), 8 * 1024 * 1024)
        self.assertGreater(sum(len(p) for _, p, g in batches if g == "varied"), sum(len(p) for _, p, g in batches if g == "regular"))


if __name__ == "__main__":
    unittest.main()
