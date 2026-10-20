/* Compile against the pinned native source headers, independently of C# layout. */
#include <stddef.h>
#include <stdio.h>
#include "SDL_audio.h"
#include "SDL_version.h"
int main(void) {
    printf("SDL header=%d.%d.%d; AudioSpec=%zu; CallbackOffset=%zu; UserDataOffset=%zu; float-format=0x%x\n",
        SDL_MAJOR_VERSION, SDL_MINOR_VERSION, SDL_PATCHLEVEL,
        sizeof(SDL_AudioSpec), offsetof(SDL_AudioSpec, callback), offsetof(SDL_AudioSpec, userdata), AUDIO_F32SYS);
    return sizeof(SDL_AudioSpec) == 32 && offsetof(SDL_AudioSpec, callback) == 16 ? 0 : 1;
}
