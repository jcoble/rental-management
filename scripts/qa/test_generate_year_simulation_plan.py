import importlib.util
import sys
import unittest
from pathlib import Path


MODULE_PATH = Path(__file__).with_name("generate-year-simulation-plan.py")
SPEC = importlib.util.spec_from_file_location("generate_year_simulation_plan", MODULE_PATH)
assert SPEC is not None and SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)


class WebNativeControlPatternTests(unittest.TestCase):
    def test_matches_native_controls_only(self) -> None:
        source = """
            <Select.Root>
                <Select.Trigger />
                <Select.Content>
                    <Select.Item value="house">House</Select.Item>
                </Select.Content>
            </Select.Root>
            <select name="type"><option>House</option></select>
            <input name="address" />
            <textarea name="notes"></textarea>
        """

        matches = [match.group(1).lower() for match in MODULE.WEB_NATIVE_CONTROL.finditer(source)]

        self.assertEqual(["select", "input", "textarea"], matches)


if __name__ == "__main__":
    unittest.main()
