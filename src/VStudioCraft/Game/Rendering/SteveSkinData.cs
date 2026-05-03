namespace VStudioCraft.Game
{
    // Base64-encoded bytes of the canonical Steve skin (64×64 modern
    // classic format — top half is byte-identical to the Alpha 1.1.2_01
    // 64×32 layout, so the same UV regions work for either format).
    //
    // We inline the bytes as a base64 string for the same reason
    // AlphaTerrainData does: shipping the PNG as an <EmbeddedResource>
    // has been observed to silently disappear from the deployed DLL on
    // some build configurations. A const string is part of the assembly
    // IL and can't be stripped.
    //
    // Source: minecraft.wiki "Steve (classic texture)" (the post-1.8
    // 64×64 file). Regenerate by base64-encoding Assets\steve.png if it
    // ever changes; not hot-reloaded.
    internal static class SteveSkinData
    {
        public const string Base64 =
            "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAMAAACdt4HsAAAAdVBMVEUAAAD///+qclmbY0mQWT+PXj6BUzl2SzM/KhUzJBErHg0kGAgmGgo6MYlBNZtGOqUFiIgElZUApKQAr68KvLw3Nzc/Pz9KSkpVVVUAzMyUYD5qQDB3QjVJJRBCHQooKCgAf38AaGg0JRIDenqzeV63g2tSPYnw8BGEAAAAAXRSTlMAQObYZgAAAo5JREFUSA3t1oWOM1cAQ+HPNymlzMx9/ycqV/Qz0eJ1GTKFya642iOy6MjXgwHYaVTOACcOYwugTRsEdSgDQMNgBjIvLkiqKcAlBDStQrnoBpVQYw7MXGKD0qSjKrlwg6CJ9B1cqwPJTiVVRUJaoZE2j1eP0CTaIaSdlaSYxLQmaKuzevL+hx8cn76bzppKlKwfIa1Rx/3M1+QZmZJWGvpk9QiTHB+nHV/ni/bnzGRExRrbInUUX77ry+RIUxrSyOoGIzk9Gpu3s3k/+WCzeXszjk6TAbFOnp465sgbN99peq3NHDMGT+Nk9bEOvIhn7XwHePdaon37IeAB4HV6r8x/uhN3HgOQEP9IJf8oeGxnlRSt1QYVrSVFut8gL2nCTEPMLUbvRl/JxKiSoMSLeIBbgK0CEIaKvHzPy6Ei0kAJoioAg0AA+vrrrbz6UvTnPFGmSnSCqOUGoPI6r99OJPpzvkORFwH5Ld/+q6AVaSaFNlBAgx9I04AurkJISyq3XneLBn7OCaFFtUnVQiAQCW6RqIZbIFSqyHKDIfYIKY0mBBRQEC2AbQUlBYQZUVJBtaSg9tjuPFawAyeBtkkJ1bQKNHRPMLMjRZvoeVPifDP0XIxUzweQghTAVhtUKUVEx2SKdHYzh7RRSdMQV/yfiAN5A2iRcNMVV6zcSC/iNY/dALwW7pBzLNmy5J8/96OtgwXLz710wuUbtKPNQRu8pInSCH0ND5QAt+wz/AOVUIgEoWg5TIAAMCtRlcM2yH6VvLyQJwC6FNAwBSb9AZqmIGfrDRqRIpVKSxV0fYMgARkjEJRILjCigIAC7fqIFRoUUCCFdcHOY1rswCMU6EGC5f8CSAHpqsDyfyENoqJiwY8icHkmoi9YwQAAAABJRU5ErkJggg==";
    }
}
