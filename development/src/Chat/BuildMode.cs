namespace Chat;
public static class BuildMode
{
#if LOCAL_PROTOTYPE
    public static bool Local => true;
#else
    public static bool Local => false;
#endif
}
