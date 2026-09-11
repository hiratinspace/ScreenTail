"""Guards the /research layout that ST-006 (fixtures), ST-061 (prompts) and ST-062 (eval) build on."""

from pathlib import Path

import pytest

RESEARCH = Path(__file__).resolve().parents[1]


@pytest.mark.parametrize("folder", ["prompts", "eval", "fixtures"])
def test_expected_folder_exists(folder: str) -> None:
    assert (RESEARCH / folder).is_dir()
