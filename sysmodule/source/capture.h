#pragma once
#include <switch.h>

#define STREAM_PACKET_MAGIC_VIDEO 0xDDDDDDDD
#define STREAM_PACKET_MAGIC_AUDIO 0xEEEEEEEE

/*
	This is higher than what suggested on switchbrw to fix https://github.com/exelix11/SysDVR/issues/91,
	See the comment in ReadVideoStream()
*/
#define VbufSz 0x50000

/*
	Audio is 16bit pcm at 48000hz stereo. In official software it's read in 0x1000 chunks
	that's 1024 samples per chunk (2 bytes per sample and stereo so divided by 4)
	(1 / 48000) * 1024 is 0,02133333 seconds per chunk.
	Smaller buffer sizes don't seem to work, only tested 0x400 and grc fails with 2212-0006
*/
#define AbufSz 0x1000

/*
	Audio batching adds some delay to the audio streaming in excange for less pressure on
	the USB and network protocols. A batching of 2 halves the number of audio transfers while
	adding about a frame of delay.
	This is acceptable as grc:d already doesn't provide real time audio.
	To remove set the following to 1
*/
#define ABatching 2

typedef struct {
	u32 Magic;
	u32 DataSize;
	u64 Timestamp; //Note: timestamps are in usecs
} PacketHeader;

_Static_assert(sizeof(PacketHeader) == 16); //Ensure no padding, PACKED triggers a warning

typedef struct {
	PacketHeader Header;
	u8 Data[VbufSz];
} VideoPacket;

_Static_assert(sizeof(VideoPacket) == sizeof(PacketHeader) + VbufSz);

typedef struct {
	PacketHeader Header;
	u8 Data[AbufSz * ABatching];
} AudioPacket;

_Static_assert(sizeof(AudioPacket) == sizeof(PacketHeader) + AbufSz * ABatching);

extern VideoPacket VPkt;
extern AudioPacket APkt;

Result CaptureStartThreads();

typedef struct {
	UEvent Consumed, Produced;
	Mutex ProducerBusy;
} ConsumerProducer;

extern ConsumerProducer VideoProducer;
extern ConsumerProducer AudioProducer;

void CaptureOnClientConnected(ConsumerProducer*);
void CaptureOnClientDisconnected(ConsumerProducer*);

// Returns -1 if the wait was failed, 0 if the first event was signalled, 1 if the second was signalled
static inline s32 CaptureWaitObjectsWrapper(UEvent* first, UEvent* second)
{
	Waiter w[2] =
	{
		waiterForUEvent(first),
		second ? waiterForUEvent(second) : (Waiter) {}
	};

	s32 out;
	Result rc = waitObjects(&out, w, second ? 2 : 1, 1E+9);

	if (rc == MAKERESULT(Module_Kernel, KernelError_TimedOut))
		return -1;

	if (R_FAILED(rc))
		fatalThrow(rc);

	return out;
}

// If this reutnrs NULL, it means the wait was cancelled
static inline ConsumerProducer* CaptureWaitProducedAny(ConsumerProducer* first, ConsumerProducer* second)
{
	s32 res = CaptureWaitObjectsWrapper(&first->Produced, second ? &second->Produced : NULL);

	if (res == -1)
		return NULL;

	return res == 0 ? first : second;
}

// Returns false when the wait was cancelled
static inline bool CaptureWaitProduced(ConsumerProducer* prod)
{
	return CaptureWaitProducedAny(prod, NULL);
}

static inline void CaptureSignalConsumed(ConsumerProducer* prod)
{
	ueventSignal(&prod->Consumed);
}