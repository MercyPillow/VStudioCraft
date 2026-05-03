namespace VStudioCraft.Game
{
    // Base64-encoded bytes of canonical Alpha-style mob skins.
    // Sources:
    //   * zombie / skeleton / creeper / ghast / zombie_pigman
    //     — Mojang's official bedrock-samples vanilla resource
    //     pack on GitHub.
    //   * spider / blaze — InventivetalentDev/minecraft-assets
    //     mirror of the Java-edition vanilla resource pack
    //     (bedrock-samples ships those two as TGAs / not at all,
    //     so we fall back to the Java mirror for PNGs).
    //
    // The 64x32 layout for the humanoid mobs is byte-identical
    // to Alpha 1.1.2_01: head + body + right-limb in the top
    // half, left limbs mirrored from right at draw time.
    // Ghast ships at 128x64 because its 16x16x16 body cube alone
    // needs 96 px just to unfold.
    //
    // We inline as base64 strings because shipping the PNGs as
    // <EmbeddedResource> has been observed to silently disappear
    // from the deployed DLL on some build configurations. A const
    // string is part of the assembly IL and can not be stripped.
    internal static class MobSkinData
    {
        public const string ZombieBase64 =
            "iVBORw0KGgoAAAANSUhEUgAAAEAAAAAgCAYAAACinX6EAAAABGdBTUEAALGPC/xhBQAAAAFzUkdCAK7OHOkAAAOMSURBVGje5VhN"
            + "axNRFJ3fkZ0LSzbiB1ikgtlpW4ILFxEaNymILUGkQinaRZfiKm66KEh3Qnf9I/lPz5zXOeOZkzeZBINMxsLh3bx7M80579773pss"
            + "q/l7cfwgPD98GDiqrWMK8GWb/kcyeydPCwFoK9EU4GuFAK/Pt0ukaasoz95slcD51ggAqACArn5rBeBKMws0I7QEUgK0ogRSqw4B"
            + "vBG2VgCtd61/EvdS0AYIXyt6QNXWx1LQctBdYyME0K1Nu73v76ltzle/6hm+Y1AgxDZmn3fS/IFK4PjbIMK3RG+I+hzdPVJjozLA"
            + "CVCEDz8G4ePlMHz5+T6czMZPl+/iXGo7XHRo4naqfaVxNa5pjBErfnC+G4kTnMOoW6I3SB1dLGZII/d5/ljMgWin04kgeX6GT8XS"
            + "rNEsoM8baKNKwLu37vmsfcbys5Je1AvUpzGNKQECB5jUau2ePon2/szez+cx5+mM7+IZvntoZmimNa4J+uqkzvq0fc/3DKo6GPlz"
            + "G3cuGOxNg6Lb7ZZQ+4DpNGwf3E8CvkfdzwtR+/zb2/iciJntmdUIAfxeQKxFAJLPsfZtddME8BPmf1cCf3338B+w8/h7eLnzqwDm"
            + "tu4NC6gPyC4u/mA4DNn1dchubuZOl5iLPo0HMJd/J5IDYGv81dUdYJsAtf51CgA7KcDR0R1gk8jsxxedHkRITuMBkqVfRUgJ5nF1"
            + "/lUFcIK1AigZFSAXYW51NZ4CkLCS5/ivBMDKVwkAnwqAz8yUmPYkA9vJ6Opjvipe49T2eKQ6swZ2nX8ZAUhokQD0c6Rd/GPCVzAl"
            + "gMczUzxzpMkRPIYT7sf9RFErAFdSBdAV1r6g87RLZLymVQDaKhjsGgH0YoZ+wgsZRlzJ9RYLYI5A3FICKCnPAJ1L+ecIefqrGADj"
            + "mbZVQuXxfrwGMV7PMXKedxj4356+iuRhr00AjVtqF0jVP7u6NkEXy8Tw6zRXlwLQxwxhaTBupSZYJYDXvWJOgKpmxs9VgnnpmABM"
            + "c5JnKeiLWQqwUgn0er0AVBHkPOPG43EJBSE92OhKOrGUAH4WkDl90wSyJE8h/DZK4ktnQL/fL8iR4GQyCV/PzqKtPsS6ADwncHQB"
            + "i0whwRlpPVfM+XPhOK+XHb6fZPrD1ldv9KMHLJ0BIKUAKZIH3D8ajUpQAVLbqJYSiC2KZ4yWor5u4ys6fS+pPYJvrtkEMdbx/w1l"
            + "xzkXWEm9iwAAAABJRU5ErkJggg==";

        public const string SkeletonBase64 =
            "iVBORw0KGgoAAAANSUhEUgAAAEAAAAAgCAYAAACinX6EAAAABGdBTUEAALGPC/xhBQAAAAFzUkdCAK7OHOkAAAJSSURBVGje5VhB"
            + "jsIwDOz3+BDSXrkv176A04of9ANojzyhDyk7SINGXjuN2qYNFMlKmtDUnthjJ00z8mvbdoCcv88D+1YwF0nz7j8Y2HXdcP25PlsV"
            + "AhC9+xEAqMERCLsB4HK5DPf7/dkSjF0AAGNhNEFQj/h4AGAkdh1Cg9HfdQjkckBq7u0AoOt7AHipcCxDVOXilt09tvfGUvNc11tf"
            + "56sDwFP293Z7cQD5AGMpECwA0dzmAMC1Na15hkP6vn+FgEoEBNfl2p5grhoANL15ho6JJctcqaLUpWBXQGDW1Q+Hw1O42/qsKZLv"
            + "2rNCZHwVJKmMzZ0/nU5u/GLcm4NgnJ7gZYboAFUFAF/H40tJGqrKc8et6H/wDo3XNcdkcwCsolG6S8W8kp+uhbXHpJp6gB7AtiTn"
            + "MGws/wDE1HuqYxHFin5AKsu/XwOh8XxOAVBsg8jGUVui8FKD9dkrjIrrR1ckgdnnUmlXwyB1tC6unx50vDaHOzTNRbGsRmoa5G6i"
            + "z6qRfRo6R79sAOylx5xSFQYoCOjn8Ir3zRL6/VOW53xUdop+jovpTtoDD9OlHfc8wIaCjun5wraLXXnpoUbz+VRe0VMf+2PvUQdv"
            + "LQ0TbRchpRTTTuEArqulsRJaxAE8gVovnKNfdmEStVM9QG+TdCzHc2waTBHhInVAFF9TAZhDgvabGkJeu+h9wNbndc+tiwNgS+C1"
            + "SuJUubuqfqn4X/vCwjMqtftV3CmCtSn2dkh31t4y2fe0wOHxG3Nvca2uKYs1hdYSOqapTcnWuzVeg4MeTnd5NL9NS2UAAAAASUVO"
            + "RK5CYII=";

        public const string CreeperBase64 =
            "iVBORw0KGgoAAAANSUhEUgAAAEAAAAAgCAYAAACinX6EAAAABGdBTUEAALGPC/xhBQAAAAFzUkdCAK7OHOkAAArsSURBVGje1Znr"
            + "U1N3GsedfbHTy3Q6s7pVW7feqqNUCCiXgNxDIJCTCyQhJCQECFcVwQtClboiC6gIJtwUKLTWpVrqKhchRG4R5I5JOBpjMZYZ1pn9"
            + "M777Oz+mvtg37hsLZuZMknNJzvP5PZfv85wNG97yCr/rh4vOEtxlO9HmbkI32wWRKQbDyw+QN6NG2oQUg2wf0lwMjj3RIrlBhfue"
            + "2xhfGcIwa8GG9/0VdNUPEbUh+DuBkPUiESWeLIhr4zH9ehxJzYlgesNhXe5GvjMFJ8eMGPNYITdJ0O3sQpfn5vsPIG4sAAlTIZC3"
            + "MjjNGlE0aITH48E3z3NRuKTH+NIopl7bkD6YiJ887Ui5pEbpr9lImYxFt/un9x9AzhMV9I+lUDUokb4ghW5AgsfLNpxxZiHbpsKg"
            + "qwd99ntgWRZLS0v0/ZQzE/IeARrtl99/AIlD0RD0BcLmGqWunmvVUCNLp47gqEVPP5+cS8ep2Uy43W443U+Q/kgOQ7UBA+677z+A"
            + "iGo+dDYpWt11UDUl4fXr1zB+Z6SrndWXTBKdFWWLR5Dfo6MwZDVSCimrg+SDZev7D6Dr6Q+45ezAhs4NkDxkML88DY1Jg1RTKgxN"
            + "BqhNyZAPRsOxPA9FoxxacwqtFAVzqTg9Y3z/AciHGWQ8VlAAH9/8GC9XXlAAE+5RJH7HwEDKYPaSAqpBBcQz4Tjq0iDpmhxnPEac"
            + "Gsle/wDEjjDIHgrQ7myEYVIGzWACUms0yJlKhn5chs4n32Pjz5+C6YtC+3Q7dfNKz2l8u3AcElMC8h1qFE6mo/nZVQrpq+4vEVsT"
            + "jbwJDTKHFHjpWYJhTI7j1nRaPQbc3Tjm0uLIghZMXcLaA0oeEUF2RYqr7HmImoWQWCNQw15E7MMQKoD2N+wlYDSIsgTi+Gw2GLMI"
            + "RR49sodVFEZBvwFxZgEe2h9SANzW6+7Cmcd5WP7PSxja9LCtWJE/rqfHdrXvQslcNqkgyai3V689AHmDFC6XC3eJaLnO1lK1F1kV"
            + "jshrBID9FOImA7FduRn8FD80uK9CfSMZX0q30O/cimZ3ZNPsf3P+Jnx7fPH5L1uRWqGFulUJ1uXE2V9zcYYYbFmwvAGUPC7G5cVK"
            + "HJ/OXHsAJfM5eLXyCmpnHFKfiYl6uwWL+x6aibGtbhOY+TBqMDkVuQ4V4nqD6efNIZtQas+l3sCByFxUQMMm4IXnOb1+kogjzkPO"
            + "efJQPJYHq3sAW+5vwie3P0bbbBvCG8MgsAXgT20bPlhTAIa+pFUpO6+lq8O3+EPPiiFeCEUFW4bLJByyLWo0kjK4TbYJf2M+wwRr"
            + "g+iqEGlOBsq+GGpoxW8ncGpo1Rs4o8/a89HM1qCFQMzpTQHrXkTBdCpOjWXTPkLeLEXOU+Xae0D6RCKuk5uMModTABu7/oL0OQki"
            + "ewPQudSGf7KtSLwupS7PrTy3bVdtQa5bhTF2CEcndFT8yFoYlEzmU51wciEDJx8ZqaGcPJbWMVQx5jxWQ9+sR8HLVCgcAky5x9ce"
            + "QHgjHx1sA47PH0HIdT6U9UmIuH8IFSQBmu2VtKvLvZnzxvjfN8OwHIk3JGDa46kH3LK3QmFKgr5VhxOjmVhZWcEcO4MJ0ivoW3SI"
            + "qYlC0UgG3f8N8Q61RURg6NaBB8xJqQGcwLnpakI7SYKKoRjcc96BqCGWrvTF34pozDew/6D7uTA4XBeEPFICFXPRuOu8hSYSIlwC"
            + "5Yw+Y8vH4rIdWpMWhQ49jjm0SDTLSaK9BRtRhwWP0qBokaN0IXf96YSkFhnaXWaaBC+wpdgp/AJSuQTR0kjw5f5vveG4hiiwy07a"
            + "ILncLhQMpeEB6Qk6Pd/DyvbjR2crJNfjaUhwoWAcVaLD2Yzip0YYSNO15gC4iiCsjcR5UgLN9gpsCfwMu+U7wZRGYxvz17feIFc6"
            + "g674otF1ieYAnVkHcX0cau3lyHqugNwRTXVDi9OMoNoA+HXtR7glBOWuIiQMha89gLSqNJx15CF5MoaWs62ijfAv3o+9qt3Yzmx9"
            + "6w0ergxAwgwf+gkxFlknslkF9E/FkDcRyewmAEYE6HS3wUj2+1u8ViuP1R+powxmVx6vPQDHyixyXqiQNcyNtn7CTskX2Ba7GZv8"
            + "N2IHs+X/8gD5fASUFiHs5LcKHToUjWciqo5I7oUwpA1L0bfURQDHY/PPGyEaFaJ6sRwXyHVcxfjDDeaamiy7Alq7GOZnNXRFdvR8"
            + "gZRqNWzsKI6N6cH0hKHKXkxDIq4niOYG4QAfMX18OioTPPKHcCKQnsPJ55RH8UioFmKWnULIWT8YLHKcI/u5Y+o+Ea060mEB9H1y"
            + "mguyF5Po9RfIcS6RepfuxZ6kHTQPvXMA3J9KnoQj+l+BuLZQtdr13fkQ2jYNFt0OJF9ToNCWgW7PbaLjd6DMXgpJQzzNERF3/SAv"
            + "F4F3fjf2FWxH4sUE7FHtQoYtCUmNMjxzP0WAyh+xZaGkAtxGYO0hdBDNIWlIoGIqf0RL+wbj80Raeo9eyYOXdh94J/ZA0hhP4b5z"
            + "AIwpHkbS7nI34tN4ALwb3hggq5LBylE4k4bp5XF687J2BtvvbaONkf9lb/DrDiGYbPwrfhBa+HT1Ay7xcEDjhZKa09DMxSFuOoQC"
            + "DakPwIEcL1oGW5zXIGmKh6pZAV1TKmRXJTCMyFFSdRo8zQGiLWRUmeodDGmvw949gKIZA759dQTPlhehXxTj3Kt8GJoNOLGYjtOT"
            + "OXCyDmhn45HnUcG/ZnVEHniZRw0ONRPXJ+7P2ENp46ScEiDFFQ++LhC+F/bhcK8vxEOx2Nb7Gba3fg6ezhtN7CXEkFLJTYuSv1NA"
            + "OReDkEoedJUqKBcEUD2NXQ3D/q3Qjf0BZbH638U4Nbqq4fOmU34vdW/e55dmkTYkReaYknaL10lcSonBcQ+DEUxcWvLkMAQPgqi7"
            + "FpHujpsaZ9Sl42CJF/gneG86wLCBg1BWyCAjIeNf4QVe8R7sVn8Jnwt74E1cvpJcr7QJIbKG0vM/+P7POFjs9e4BnHmcC12jjgL4"
            + "X+O5d27jGqLEfgFdfe45QfwUH8IHAaggq84NTAOqeTSByeul0FrEVOwk2WJQxZ7Hh40f4qMfPoKw7zC9XmCOgGQuHHGW0NXv3PWX"
            + "felvRZnCIOoJh6FJB171Phou636iFGwOoMZzSZGT0pdJBxlWHoQgkh84o8TTwQitPIjimiJ6TNQcg8wRBe6xnTRswhr4dM7A/YbV"
            + "3Y+y2m8QogyBtlWNe8s/r38AvPNfIb1cDeHFYCRcCce3FWW0y6MDFpLxpfOhyBnRoLDyCILP+0D1MJaW05j6KEgWIpHhkmFqZRyK"
            + "6SgaDjkV6ehZ/gX32Tu0HV/3ANR9DI3Z/dZdUJGEl1dhRFC5N9QkmXGzgEskDFItDLqcnQgu8wGvaC8KKvOwR70L/Ive8D/phdIr"
            + "p+FDSqlP8V7qGYYZBnJ7FPWqdQ/gYO/XFIDvDR4OD/hRt/763C7sL9wJE+kHuOQWfm312aJuUAppDUMrjGIugvYLwgeB9DliVEMY"
            + "BP1BdCKVPCdAVPchqg7XPYBPf/yEAsgk+sC7cT/VBjE9wSi6cgRFVUcxxFqQ2qKB1CRGQn8ITagau4hUjgBEmkOgaJVSUBw4ab0Y"
            + "gqZIRBJRdIu9QecR63+sPhVK5KuJxmsV2bhV4ybKd5wd4Gl9cPZlLlF6SZA1MbSMck+SlP2xBFQAkh0xUM0JMeq20gFMHYHzi+tH"
            + "xA7y6fcYU8Q7B/BfFltjLG8pudcAAAAASUVORK5CYII=";

        public const string SpiderBase64 =
            "iVBORw0KGgoAAAANSUhEUgAAAEAAAAAgBAMAAABQs2O3AAAAKlBMVEUAAACSiX+MgndgVEhPRTxHPjWoDg49NC0yKyYmIBsmHxoa"
            + "FA88AgIiAADotIQjAAAAAXRSTlMAQObYZgAAAgpJREFUeNp10rFu1EAQBuB7B8RDUCIs0tB5rUuUNAivfBKvkI5b/eNLyVkzg6hQ"
            + "YGfyAkn6oDwAFUUqUvA4rHTGthTlb7b4P+3urHZV4uqqsuKbHdNNz3S9A5V1NUVIVW0leQfOCuQvhNzTDJhF3W6RlUvJV5R7przj"
            + "BSBxu5KbHmV73OJaUc7DBDIVIKs5zuSU4nSIkYotgSUox25z0GAtfe+qk+GWN0PCARhI1fgwyJhjpOb7QDQBN0DVJ9BRiJmhB8CQ"
            + "AlhlAfZNaxAfb8ziwuSLQeqmrdx8BMJi0qsvBrms6hP3AuZIFxNYRJBj6L5W9d8UYg8SGcGmjSlRFuOB1/vqz7v6mLYbcscIEoYO"
            + "AjNLoB+/6urtbwarGabB2q4juBnR5dnj+fnZ4wlDM2QEbQhdVHXOoLr69PBwVFfM0sNmgEE9g5Ve34U3dTh9IcZu/0Gsw2atboDL"
            + "6f37u48vL+7E4c4jaD80YU2ult0v/NvP01d+707CGMG2DVVH5uNrPk0a2oog7vYM4G1qgN6FngEAgZmc8aQKMTSBgY62KbShCwIK"
            + "aTODGPhztU6csA11FTgJQhNpBuGoNM2QIGDsQx0ktfF4ARKDIiKYmUAhdMZtxzIDYN+g5xLJVO4Aw8BYAG9L7waQeiaQGwlIZ5AR"
            + "u9g7a9Yc0wYFK+kCeAbgDvfegdK7qyy+4z9b0kGP3NmJngAAAABJRU5ErkJggg==";

        public const string BlazeBase64 =
            "iVBORw0KGgoAAAANSUhEUgAAAEAAAAAgBAMAAABQs2O3AAAAJ1BMVEUAAAD//////4T/+Ef/1Sj8shf8lgDReACrdQGLNAFsMQBf"
            + "AgExDgs044iSAAAAAXRSTlMAQObYZgAAAPdJREFUeNrdzzFuAjEQheH3vNR4xtCkibyQA4CSCyAlfZocIjdKm4sFxAHwGKVmQo21"
            + "Ts9X//IbE1evBfpBjG9ozXDlJZkKTkAr4IpShNVkMjiJ7wuUkwEJhcAmAzFGNcTJoFisJZph6heJiG7xDLT4XKRC4KiubvSf24kY"
            + "6hiLnVPMVfE+NhPbo2Y3YVBfPA2/Q3PkchGGx++H4yZvd3GdNrjBr32FeDzPUdN88Mvn7QRHHiSX9SkMwnxYNTdkq7sDlMsLkZHR"
            + "BEwBGaBpKnQ6GqsVegLIfrDOfOkGI0W7gQTN6JhJQlcYCOm+oJyjHyD+F7jg7v0B5TtIgi1mkx4AAAAASUVORK5CYII=";

        public const string GhastBase64 =
            "iVBORw0KGgoAAAANSUhEUgAAAIAAAABACAYAAADS1n9/AAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAAEnQA"
            + "ABJ0Ad5mH3gAAAGHaVRYdFhNTDpjb20uYWRvYmUueG1wAAAAAAA8P3hwYWNrZXQgYmVnaW49J++7vycgaWQ9J1c1TTBNcENlaGlI"
            + "enJlU3pOVGN6a2M5ZCc/Pg0KPHg6eG1wbWV0YSB4bWxuczp4PSJhZG9iZTpuczptZXRhLyI+PHJkZjpSREYgeG1sbnM6cmRmPSJo"
            + "dHRwOi8vd3d3LnczLm9yZy8xOTk5LzAyLzIyLXJkZi1zeW50YXgtbnMjIj48cmRmOkRlc2NyaXB0aW9uIHJkZjphYm91dD0idXVp"
            + "ZDpmYWY1YmRkNS1iYTNkLTExZGEtYWQzMS1kMzNkNzUxODJmMWIiIHhtbG5zOnRpZmY9Imh0dHA6Ly9ucy5hZG9iZS5jb20vdGlm"
            + "Zi8xLjAvIj48dGlmZjpPcmllbnRhdGlvbj4xPC90aWZmOk9yaWVudGF0aW9uPjwvcmRmOkRlc2NyaXB0aW9uPjwvcmRmOlJERj48"
            + "L3g6eG1wbWV0YT4NCjw/eHBhY2tldCBlbmQ9J3cnPz4slJgLAAAJEUlEQVR4Xu2cS27bSBCGywYSih7HDkYxo4UDIYDgRQDfIT5N"
            + "gDnQADmNc4pZBNoEnoWjjIKY1oiktcgsRsUUi1X9oJoPPz7AQNxsd3VX/V39EJU9YBRF8ZP+nmUZ/RVevny5VynYkR8/flTsIf+u"
            + "VgAA8NvhYbDyOI4rz1zg4wcAiOMYsiyrtCfV40j2oygK6k9f9nnBQybLsvKHQ5+Z6gEJtq0ex7d+FzwqAQyJoYjgwQggX695kREa"
            + "gKEEow/2+Jrvy65rmLYHWC4WMDo4MK7pFN/6QNZkTQCrNOVFjTg8OuJFAFv7u/pvV+6lAPL1GsZJUik31QdPAazSFIo8BwCAzY4i"
            + "eLYNfjQa1YQwBAE8mCWgCTzwSJHnsEnTnYMPWwFtiKAomv0uuXcC8F3rfQmV9iXabLsp3gIY2jGGoolDKwdlFoaY+Zw22gzBvuQA"
            + "DZ+6ITAF7okw7IMhsFr5Q0G6mXts7IPiCB58+rtUvy1wF08JlU75GB8jZQZAZ/A1nv/ue/9tQxPT6OAAwGMZaCKKOI5V+13Qp22k"
            + "3AS6dMalTiikczs0CLSrgPogxATaldoSIM0KemFCf7pCWgYkuDhsf9flGIbKvi2lczF0CV8GlosFq2EG/w7b4fQ5tqGwr81oKROY"
            + "ypvC7VJwGdikqXPwsR6d/dpyAhb7j4HaRZCrQ0KJwLUdmt7xfl1juVjA7fU1gGH2I67226BP28g+zmg6s11FEAKbLfqhD4rAFlQq"
            + "FtPsBwf7D51aBrCJAMu1521AZ7w2+7GcBt8mlCeUPQAXAQ82/71taBYwBZWK48VkUnn2hEwlA5hEQAm9EXQFAyyldSoMUz2KNLYu"
            + "6ds+cAGAIgKKVLYLru1pqZ/jGnzE1X4b9GkbqdwEmmb9UDAF1rQ8DJEh+Ll2E4jwfQHH9KwPTMLQkMYNHtnGhzbaDIG6B9CwCaNP"
            + "moiAw9/bCwlvWxNgl9Q+DXzi/xc4nx0dBZm12E40GvFHg2BPeyvXlV2/KlYUxU8X8WlvA3O0t4MlbHueUO/w8ZmPxAN4K/jeCCA0"
            + "NP32YR8GIoDaMbAP+l4L+7bfJ4MQAHQcBMkWPQa3TZe2bAxiCeBlHNPlFD6T1vNY+Bq3C/g30tJA7dA6mn0bfS8Bzl8N4wMGwwBN"
            + "jmsKb9MUXF4XkerasI03pC3KP9++AQDAq5MTAGIHN6a4sUQ7375+rZQjWP/k9etKOVIKQBtIG2jO6aoPkv2ubCO8D9w+vtDy6uSk"
            + "8my5WJRHSiqCv798Eb9/iPV5OdqvfC9gtX3zxvXtm6Zkwr0D/13CpV8udbgt/rsL6KdVmsJdUfDHNbAuzkhqk/ofY5Cv15Cv17W+"
            + "Sd8zxNm/UsbO6wOxWW4C6TdibWft0PBBSrgEFvGp25QXx8cAW+e6CGCcJKV/pfsFLKPB2hDBUPjLr1oZGMqRvb7O4aBsnDjLxaIc"
            + "xGQ2449FrudzgO0tnEnMLvbbRLOPk3GTpnCYJJX0jWOj5as0LWc/9REtp7eRdOkYzDGQgynWN/hA6uLLpF1khNDYZm4TijyHIs8r"
            + "WWWwAhgnCYyTBCazmVfwEfw7bKdt6BrvsiSY4Bu2EESjUekL2v5gBXDfeHF8XK7fPgLg6b8tNFEZvx5Od6VtYbLfNiFtP4+icnZp"
            + "zuZkWVY7HXRJlmV6BqC70rZSaMgANKXvPthOB22z81UwZ5WmzjNAY7lYwJ8fP/JiAAB4d3YG7y8uap+vF3kOny4v4a/PnyvlyB8f"
            + "Puws5CZjw+wZjUbwPIrgeRRVnuONJgd3+3z/43sKwNOEVA6h9wBxHIuXDj6gw96dnfFHJZ8uL8sdLf58urzk1UqwrV2XsiLPazd4"
            + "NnzvC5qgiVIqp8GH0PcApitJDnUkt09TIT3H+oDHqEMy63mftHO4BDrudDp1qu+Cyf71fC7eY0gZgJbzjIGi5+1AW/cAdE0zIQ0a"
            + "wY0UDxgeY06n0/LfUhlFa8uXNvdCEr6C1+rb7hNEAeDOn6dMl1NBHMewSdNSkSZMIuCg800pGJ+Nk0R1CMelD9fzOWzS1Gi7yT2A"
            + "zbYpeD6C1toRTwF0jaCKdzkV4IDenp8DkLTUBz4iMIFjwDFpQWt6D6ChBc2ENF7Ty63iEnC4XXd4kDGN8nJ+dOEiwJkhYZpRFL6u"
            + "SUGQynhfJbQ+0EynBZ+Oq8k9gGYbLIGTkGLmQk0ATeABpiIYb/cEXAimwVO4E3gQKPRZ01mIgcdMZwq+y16nDbhPEJ8xo/+D3AMs"
            + "yYc2v795U551eZBdnMg/sWoKto2zgi5hFOnEovX7rijg+9UVgMMnjS6YTgHa7l07HfDxupZX/o+gJsRxDKfTaZmmv19dletmZvhP"
            + "pXyC76NspMjzymURtildIEkZjPf7ej4vgz+ZzeB0Oq0JpQncLxTeVzDMfj5eilSO9SsZgM4SrhgT1BFZlnl/Hs+hTtEU7MJdUVRu"
            + "3lzscWh2m8xmtbHaWJJXuPhNYEzuTYAJH2PBJ4OWGfhYKSvlBjOO418CaBp8CncOdlYaoBQMyaHoQGkATbHZ5svFOElqY3Plrijg"
            + "9uZG9QH6iT/XgqYJoAkVAbhCU3dkuN/mcAf7pH/e/i7w4CJcZLYxwLZvd0VR8Qfvvw0UgSvahGiaKZ2vgvk6iYOmV66SGDg+wW8L"
            + "VxFIYND5FTVdZ21tUEwCkMSvZQCtXGoDiePYLgA+46XB0TVfywoUaXaBMMM4y8Wi/NCHf+qHH/i8v7ioOYHTxD6d7U1eU5PAfkh2"
            + "tZluKgdBAKbMEEsXQRwp+DH7r+Xenp/DZDaDzdbY7c0NrLbXojxzSM7PhFOCCR58rUxDs8f7Rsdwe3NTBn8ym8Hb8/OaH0ISjUa1"
            + "DIXlGtKzQjkdYH+tGcAFbfC83SGkf8RnGXAdH4VugLWMaMoAWupeKZtDHI/0jLdFxx5EAC4MKfiIjwh8wd0/GNozCUBDEwBYniF0"
            + "zOMk+SUA/qANpJnkM/g26LNPmgC6iAXwPQAqJxqNyvQVkj4dbULqg9TX0FAb9N9dBR+pfTtYckgbSE7uyjbSVx8ku9CRbWD2awLQ"
            + "yISvYmOHablUFgqXtl3q+IJj5z7QbPF6u0KFIbXL7Wn9kvgPJflE0DXeAIQAAAAASUVORK5CYII=";

        public const string ZombiePigmanBase64 =
            "iVBORw0KGgoAAAANSUhEUgAAAEAAAAAgCAYAAACinX6EAAAABGdBTUEAALGPC/xhBQAAAAFzUkdCAK7OHOkAAAWlSURBVGjezZhP"
            + "ix1FFMXbfAN3uhEXEnAn/lm7ykJFMOBKVxFBjVklEAjZ6BAwEB0QlXGVhfhczDgiQWGEIRCEDCGrMF8hBEIgoAFdVvoU/StP31f9"
            + "ut/Qog8uXa+6uqrOuefeut3N319/1ayyra2tJNv8YjPRjqZ7QzY2/+tnj620e19eSX9c/Tbbw8X3yf+r7WNPXXwxYW+/91wamtPX"
            + "b6YQsL+/ny5ePpN++Pnz9M13n+b/tNW/t7eXbbF7Ne38uJNN/+cg4NdLHxTQEKCrTH0O/qPNE0njZSJgiIS1CBBYCIAE+iACAkSI"
            + "/h8cHMxGgEAJXCTCCXDw9y6cywYJsxGAxw8PD9NisUi7O7u5XwoBsEwkaZz65iAAEgRGoDwcdFV/Ad8SlK0jYTYCBBbpCzykqB8C"
            + "MBElEuYkwGVOCEBE8TrgW1PfqjywFgECKUDyvNoCqDYkuAIc/L9BgDwtsE6AgxdwQma2JEhc430HDwEC/9O17ZIUSYK6NzcBAuk5"
            + "oBDQXqeAX5sAYh/p1wioHYX0zx0Cngc83sdkP0iAZE1C89j2/zH7kw+GnvETAwLj6cG6ANHGfzl5slllAETyhQSrDaLVTg5PlksE"
            + "eMLDbt+6VbI7ca6+VSR4nSCLa2CetJqBHwRwGkTAngD9hKiBXyJA0nZgeA3gkryMfhIiNkQEc3JkunlYaSMC3/6G8De6h2lszaM3"
            + "T72Trr/1Zr9SbAHWSAL83Q9PpyZKVf+JddnTzz6VzesA3aefcU6AgGkeXWsGeJk2Ic9OIcBrAf2ONU/0wENArx6I/zvgWM7yJCx5"
            + "RwlMVwem/x+feb+AVhtJM86f1VyEjNoOHhWprXvaPASMGccggCCEstffA7wwchU4eK3duPcBtLGxUYysDnCIoN/HQljtZKAPUujX"
            + "JihlV4FHKV4WA1735f1PXnm5ENUjInhda2KNJ6padvfQgAAU4MazgLtw/nyPAOaPx6U2DhBywSorJ0FX6vKMwEOAjKTpqoA8wGvt"
            + "Jm40HoGQ4KcAno79avtcmnvMiF1IUHtKKETCIgGQIOP8dxKkhEzAa8ef7Nm7LzyT3NpJs9y43vj9twarbW4p2wbTwh7HMXmNgWec"
            + "SOhk39vfKgKqhdC6BEzd4FAmlvSISQjw67rza196tqae/5YAO3KcCG0S+RGjroSjEMAp8L9QgL+RqY1n3eOQ4DX8URWA54+sgAj4"
            + "3KvPZ7v0xkv5WrvPPZkfR9HKkdUpwas2VwhquLO9nW5+dqU8q76SMzAPq3g15dEeJcAXUBuAWLzv9zQWD2rjApD+/Cvbo7v3CxjO"
            + "3tpbnMDnbG7P4VmOuyrIoTxjba07SkCczL0si/cdvMZyJFF0QAhnryzW6Uif9wCehUSvC4ZOk6wiq+lLrgm1/jgB4SGXuq6RXb8n"
            + "A0D2clABIDzu+YwFIYDVs/K+THPwrS8Cou25BYX1SGjHqG+6AroFPBZ7tXeIWRbz+PfP0qiA8xoiIICQcLlrTbzriRFwnjjZF+A1"
            + "tuSWrl99k3OAroARAZ5Z41uUxybj8GCMfwiAHIEmuUGA1/eA4TRhjaKadl1CA2JQmO+V8ZMIAITs0YN/Nk8/paNnbA8dzwO1UwCP"
            + "QlrJ7t3zOYnaKy1eLWHQgUJRnPleU7gC6JsUAoDUhAJP/JVPT93HRpeaf5aCaVcBBHJPz/nrKSHhX4Mgh3HMWRKhKc6VEokj9hk3"
            + "SgAbd9l7kooEeAYnGfnLDEY/RZHX+3wD4PmiKJO37yUqJRLkRMf/owQQo57IAM2m1V5KNCY1j1GPd297gvKYL+R0azKuqMQKGycj"
            + "Eusq8JwwSoC/huqaS952QV3xjHtU4+MYMr5LL0qzV9GZpAvRnfd9naU6wEpn8oE7pvbBY9ox2JkmFChNAkj63cM+JpbKc1t8dfbi"
            + "SvsiPN38mTECHgPjlN0hDl5n3wAAAABJRU5ErkJggg==";

    }
}
