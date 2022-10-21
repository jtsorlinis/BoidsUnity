# 2D/3D Boids Flocking Simulation (CPU/Burst/Jobs/GPU)

I wanted to learn about GPGPU and Compute shaders so ended up making a boid flocking simulation in unity. 

I first made it in 2D on the CPU, then using Burst/Jobs, and eventually moved everything to the GPU, which brought an insane performance increase.

The simulation uses a uniform spatial grid as an acceleration structure to determine nearest neighbours, as brute force method would cap out at around 50k entities even on GPU. 

Method used is inspired by this NVIDIA presentation: https://on-demand.gputechconf.com/gtc/2014/presentations/S4117-fast-fixed-radius-nearest-neighbor-gpu.pdf

Number of boids before slowdown on my 9700k/2070 Super:

- CPU: ~4k
- Burst/Jobs: ~150k
- GPU 3D: ~500k when rendering 3d models, 3+ million when rendering just triangles
- GPU 2D: ~16 million

![image](https://user-images.githubusercontent.com/17734528/197126576-3a6d47ba-d65c-458f-aaf5-f0f9609cdefb.png)
