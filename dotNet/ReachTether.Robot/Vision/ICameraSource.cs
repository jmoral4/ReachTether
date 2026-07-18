internal interface ICameraSource
{
    bool TryGetLatestFrame(out VideoFrame? frame);
}
