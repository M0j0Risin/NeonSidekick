import os
import tempfile
import unittest
from datetime import date

from shelf import loans
from shelf.store import DuplicateBook, Store


class LoanTests(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.store = Store(os.path.join(self.folder.name, "test.db"))
        self.store.add_book("9780000000001", "Dune", "Frank Herbert")
        self.store.add_member("Ada", "ada@example.com")

    def tearDown(self):
        self.store.close()
        self.folder.cleanup()

    def test_lend_sets_the_due_date(self):
        loans.lend(self.store, 1, 1, today=date(2026, 1, 1))
        self.assertEqual(self.store.open_loan(1)["due_on"], "2026-01-22")

    def test_a_lent_book_cannot_be_lent_again(self):
        loans.lend(self.store, 1, 1, today=date(2026, 1, 1))
        with self.assertRaises(loans.LoanError):
            loans.lend(self.store, 1, 1, today=date(2026, 1, 2))

    def test_give_back_closes_the_loan(self):
        loans.lend(self.store, 1, 1, today=date(2026, 1, 1))
        self.assertTrue(loans.give_back(self.store, 1, today=date(2026, 1, 5)))
        self.assertIsNone(self.store.open_loan(1))

    def test_renew_moves_the_due_date(self):
        loans.lend(self.store, 1, 1, today=date(2026, 1, 1))
        self.assertEqual(loans.renew(self.store, 1), date(2026, 2, 12))

    def test_a_loan_past_its_due_date_is_overdue(self):
        loans.lend(self.store, 1, 1, today=date(2026, 1, 1))
        self.assertEqual(len(loans.overdue(self.store, today=date(2026, 2, 1))), 1)

    def test_the_same_isbn_twice_is_refused(self):
        with self.assertRaises(DuplicateBook):
            self.store.add_book("9780000000001", "Dune", "Frank Herbert")

    def test_find_books_by_title(self):
        self.store.add_book("9780000000002", "Dune Messiah", "Frank Herbert")
        self.store.find_books("Dune")


if __name__ == "__main__":
    unittest.main()
