import pytest

from edifier_reverse.volume import VolumeControl, VolumeError


class FakeEndpoint:
    def __init__(self, scalar=0.5, mute=0):
        self.scalar, self.mute = scalar, mute

    def GetMasterVolumeLevelScalar(self):
        return self.scalar

    def SetMasterVolumeLevelScalar(self, v, ctx):
        self.scalar = v

    def GetMute(self):
        return self.mute

    def SetMute(self, m, ctx):
        self.mute = m


def control(ep):
    return VolumeControl("X", opener=lambda name: ep)


def test_get_returns_percent():
    assert control(FakeEndpoint(0.504)).get() == 50


@pytest.mark.parametrize("asked,applied", [(30, 30), (0, 0), (100, 100), (-5, 0), (250, 100)])
def test_set_clamps_to_0_100_and_writes_the_fraction(asked, applied):
    ep = FakeEndpoint()
    assert control(ep).set(asked) == applied
    assert ep.scalar == applied / 100


def test_step_is_relative_and_clamped():
    ep = FakeEndpoint(0.95)
    c = control(ep)
    assert c.step(+10) == 100 and ep.scalar == 1.0
    assert c.step(-30) == 70
    ep.scalar = 0.03
    assert c.step(-10) == 0


def test_mute_roundtrip():
    ep = FakeEndpoint()
    c = control(ep)
    c.set_mute(True)
    assert c.muted() is True and ep.mute == 1
    c.set_mute(False)
    assert c.muted() is False


def test_opener_receives_the_device_name():
    seen = []
    VolumeControl("ES850NB", opener=lambda n: seen.append(n) or FakeEndpoint()).get()
    assert seen == ["ES850NB"]


def test_endpoint_is_cached_across_calls():
    # 列舉裝置很慢，拖曳音量時不能每次重找
    opened = []
    c = VolumeControl("X", opener=lambda n: opened.append(n) or FakeEndpoint())
    c.get(); c.set(10); c.step(5); c.muted()
    assert len(opened) == 1


def test_a_stale_endpoint_is_reopened_once_then_retried():
    class Stale(FakeEndpoint):
        def GetMasterVolumeLevelScalar(self):
            raise OSError("COM 端點已失效")

    eps = [Stale(), FakeEndpoint(0.7)]
    c = VolumeControl("X", opener=lambda n: eps.pop(0))
    assert c.get() == 70


def test_persistent_failure_becomes_a_volume_error():
    class Stale(FakeEndpoint):
        def GetMasterVolumeLevelScalar(self):
            raise OSError("x")

    with pytest.raises(VolumeError, match="無法存取"):
        VolumeControl("X", opener=lambda n: Stale()).get()


def test_missing_device_is_a_volume_error():
    def opener(name):
        raise VolumeError("找不到")

    with pytest.raises(VolumeError):
        VolumeControl("X", opener=opener).get()
